using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImgViewer.Models
{
    internal class AppSettings : IDisposable
    {
        private const int DefaultDpi = 300;
        private const BatchSavingFileFormat DefaultBatchSavingFileFormat = BatchSavingFileFormat.Tiff;
        private const TiffCompression DefaultTiffCompression = TiffCompression.CCITTG4;

        private int _dpi = DefaultDpi;
        private JpegSettings _jpegSettings = new JpegSettings { Quality = 75, SubSampling = SubSamplingMode.NoSubsampling };
        private BatchSavingFileFormat _batchSavingFileFormat = DefaultBatchSavingFileFormat;
        private TiffCompression _tiffCompression = DefaultTiffCompression;
        private int _tiffJpegQuality;
        private SubSamplingMode _tiffSubSamplingMode;
        private string _lastOpenedFolder;
        private string _lastSavedFolder;

        private TimeSpan _debounceDelay = TimeSpan.FromMilliseconds(500);
        private double _eraseOperationModeOffset = 100;

        private bool _savePipelineToMd = true;

        // cancellation for scheduled save
        private CancellationTokenSource? _saveCts;
        private readonly object _saveLock = new object();


        public event Action<string> ErrorOccured;

        public int Dpi
        {
            get
            {
                return _dpi;
            }
            set
            {
                _dpi = value > 0 ? value : DefaultDpi;
                ScheduleSave();
            }
        }

        public BatchSavingFileFormat BatchSavingFileFormat
        {
            get
            {
                return _batchSavingFileFormat;
            }
            set
            {
                _batchSavingFileFormat = value;
                ScheduleSave();
            }
        }

        public JpegSettings JpegSettings
        {
            get
            {
                return _jpegSettings;
            }
            set
            {
                _jpegSettings = value;
                ScheduleSave();
            }
        }

        public double EraseOperationOffset
        {
            get
            {
                return _eraseOperationModeOffset;
            }
            set
            {
                _eraseOperationModeOffset = value;
            }
        }

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

        public TimeSpan ParametersChangedDebounceDelay
        {
            get
            {
                return _debounceDelay;
            }
            set
            {
                _debounceDelay = value;
            }
        }

        public AppSettings()
        {
            _tiffCompression = TiffCompression.CCITTG4;
            _tiffJpegQuality = 75;
            _tiffSubSamplingMode = SubSamplingMode.SubSampling422;
            _lastOpenedFolder = string.Empty;
            _lastSavedFolder = string.Empty;
            try
            {
                LoadFromFile();
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Failed to load app settings: {ex.Message}");
            }
        }

        public TiffCompression TiffCompression
        {
            get { return _tiffCompression; }
            set
            {
                _tiffCompression = value;
                ScheduleSave();
            }
        }

        public int TiffJpegQuality
        {
            get { return _tiffJpegQuality; }
            set
            {
                _tiffJpegQuality = Math.Clamp(value, 1, 100);
                ScheduleSave();
            }
        }

        public SubSamplingMode TiffSubSamplingMode
        {
            get { return _tiffSubSamplingMode; }
            set
            {
                _tiffSubSamplingMode = Enum.IsDefined(value) ? value : SubSamplingMode.SubSampling422;
                ScheduleSave();
            }
        }

        public string LastSavedFolder
        {
            get { return _lastSavedFolder; }
            set
            {
                _lastSavedFolder = value;
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

        public bool SavePipeLineToMd
        {
            get
            {
                //Debug.WriteLine($"App Settings: SavePipeLineToMd get: {_savePipelineToMd}");
                return _savePipelineToMd;
            }
            set
            {
                _savePipelineToMd = value;
                //Debug.WriteLine($"App Settings: SavePipeLineToMd set: {_savePipelineToMd}");
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
                    ErrorOccured?.Invoke($"Error saving app settings: {ex.Message}");
                    //Debug.WriteLine($"AppSettings: error saving settings: {ex}");
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
                    Dpi = this.Dpi,
                    //BatchSavingFileFormat = this.BatchSavingFileFormat,
                    JpegSettings = this.JpegSettings,
                    //TiffCompression = this.TiffCompression,
                    TiffJpegQuality = this.TiffJpegQuality,
                    TiffSubSamplingMode = this.TiffSubSamplingMode,
                    LastOpenedFolder = this.LastOpenedFolder,
                    LastSavedFolder = this.LastSavedFolder,
                    SavePipeLineToMd = this._savePipelineToMd,
                    ParametersChangedDebounceDelay = this._debounceDelay,
                    EraseOperationOffset = this._eraseOperationModeOffset
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
                ErrorOccured?.Invoke($"Error saving app settings: {ex.Message}");
                //Debug.WriteLine($"AppSettings: Save failed: {ex.Message}");
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
                    _dpi = dto.Dpi > 0 ? dto.Dpi : DefaultDpi;
                    _batchSavingFileFormat = DefaultBatchSavingFileFormat;
                    _jpegSettings = dto.JpegSettings;
                    _jpegSettings.Quality = Math.Clamp(_jpegSettings.Quality <= 0 ? 75 : _jpegSettings.Quality, 1, 100);
                    if (!Enum.IsDefined(_jpegSettings.SubSampling))
                        _jpegSettings.SubSampling = SubSamplingMode.NoSubsampling;
                    _tiffCompression = DefaultTiffCompression;
                    _tiffJpegQuality = dto.TiffJpegQuality is >= 1 and <= 100 ? dto.TiffJpegQuality : 75;
                    _tiffSubSamplingMode = dto.TiffSubSamplingMode.HasValue && Enum.IsDefined(dto.TiffSubSamplingMode.Value)
                        ? dto.TiffSubSamplingMode.Value
                        : SubSamplingMode.SubSampling422;
                    _lastOpenedFolder = NormalizeFolder(dto.LastOpenedFolder);
                    _lastSavedFolder = NormalizeFolder(dto.LastSavedFolder);
                    _savePipelineToMd = dto.SavePipeLineToMd;
                    _debounceDelay = dto.ParametersChangedDebounceDelay;
                    _eraseOperationModeOffset = dto.EraseOperationOffset;

                }
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Failed to load app settings: {ex.Message}");
                //Debug.WriteLine($"AppSettings: Load failed: {ex.Message}");
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
            public int Dpi { get; set; }
            public BatchSavingFileFormat BatchSavingFileFormat { get; set; }

            public JpegSettings JpegSettings { get; set; }

            public TiffCompression TiffCompression { get; set; }
            public int TiffJpegQuality { get; set; }
            public SubSamplingMode? TiffSubSamplingMode { get; set; }
            public string? LastOpenedFolder { get; set; }
            public string? LastSavedFolder { get; set; }

            public bool SavePipeLineToMd { get; set; }

            public TimeSpan ParametersChangedDebounceDelay { get; set; }

            public double EraseOperationOffset { get; set; }
        }

        private static string NormalizeFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return string.Empty;

            try
            {
                return Directory.Exists(folder) ? folder : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

    }
}
