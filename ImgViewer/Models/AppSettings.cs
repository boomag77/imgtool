using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
    internal class AppSettings : IDisposable
    {
        private TiffCompression _tiffCompression;
        private string _lastOpenedFolder;

        private readonly TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);

        // cancellation for scheduled save
        private CancellationTokenSource? _saveCts;
        private readonly object _saveLock = new object();

        //private static string SettingsFilePath =>
        //System.IO.Path.Combine(
        //   Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        //   "MyCompany",         // <-- поменяй на своё
        //   "MyApp",             // <-- поменяй на своё
        //   "settings.json");

        private static string SettingsFilePath
        {
            get
            {
                // folder where the process was started from (usually the exe folder)
                var exeFolder = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                // file placed next to the exe
                return Path.Combine(exeFolder, "settings.ig");
            }
        }

        public AppSettings() 
        {
            _tiffCompression = TiffCompression.CCITTG4;

            try
            {
                LoadFromFile();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppSettings: failed to load settings: {ex.Message}");
            }
        }

        public TiffCompression TiffCompression
        {
            get { return _tiffCompression; }
            set
            {
                _tiffCompression = value;
                Debug.WriteLine($"App Settings: Compression set to: {_tiffCompression}");
                ScheduleSave();
            }
        }

        public string LastOpenedFolder
        {
            get { return _lastOpenedFolder; }
            set
            {
                _lastOpenedFolder = value;
                ScheduleSave();
            }
        }

        private void ScheduleSave()
        {
            CancellationTokenSource? cts;
            lock (_saveLock)
            {
                // cancel previous scheduled save
                _saveCts?.Cancel();
                _saveCts = new CancellationTokenSource();
                cts = _saveCts;
            }

            // fire-and-forget: after debounce delay, if not cancelled, save
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceDelay, cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;
                    await SaveSettingsToFileAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* ignored */ }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AppSettings: error saving settings: {ex}");
                }
                finally
                {
                    // cleanup if still the current token
                    lock (_saveLock)
                    {
                        if (ReferenceEquals(_saveCts, cts))
                        {
                            _saveCts?.Dispose();
                            _saveCts = null;
                        }
                    }
                }
            });
        }

        private async Task SaveSettingsToFileAsync()
        {
            try
            {
                var path = SettingsFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var dto = new AppSettingsDto
                {
                    TiffCompression = this.TiffCompression,
                    LastOpenedFolder = this.LastOpenedFolder
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                options.Converters.Add(new JsonStringEnumConverter()); // serialize enums as strings

                // write atomically: to temp file then replace
                var tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fs, dto, options).ConfigureAwait(false);
                    await fs.FlushAsync().ConfigureAwait(false);
                }

                // replace (overwrite) final file
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppSettings: Save failed: {ex.Message}");
            }
        }

        private void LoadFromFile()
        {
            var path = SettingsFilePath;
            if (!File.Exists(path)) return;

            try
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions();
                options.Converters.Add(new JsonStringEnumConverter());
                var dto = JsonSerializer.Deserialize<AppSettingsDto>(json, options);
                if (dto != null)
                {
                    _tiffCompression = dto.TiffCompression;
                    _lastOpenedFolder = dto.LastOpenedFolder ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppSettings: Load failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // cancel pending schedule and save synchronously
            CancellationTokenSource? ctsToCancel = null;
            lock (_saveLock)
            {
                if (_saveCts != null)
                {
                    ctsToCancel = _saveCts;
                    _saveCts = null;
                }
            }
            try { ctsToCancel?.Cancel(); } catch { }
            SaveSettingsToFileAsync().GetAwaiter().GetResult();
        }

        // small DTO used for JSON serialization (keeps shape stable)
        private class AppSettingsDto
        {
            public TiffCompression TiffCompression { get; set; }
            public string? LastOpenedFolder { get; set; }
        }

        private void SaveSettingsToFile()
        {

        }
    }
}
