using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImgViewer.Models
{
    internal sealed class Documentation : INotifyPropertyChanged
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions SerializerOptionsIndented = new()
        {
            WriteIndented = true
        };

        private readonly ObservableCollection<DocSection> _sections;
        private readonly string _notesPath;
        private bool _suppressNoteSave;

        private Documentation(IEnumerable<DocSection> sections, string notesPath)
        {
            _sections = new ObservableCollection<DocSection>(sections ?? Enumerable.Empty<DocSection>());
            _notesPath = notesPath;

            foreach (var section in _sections)
                AttachNotesHandlers(section);
        }

        public ObservableCollection<DocSection> Sections => _sections;

        public static Documentation LoadOrCreate()
        {
            var sections = LoadDefaultSections();
            var documentation = new Documentation(sections, GetNotesPath());
            documentation.LoadNotes();
            return documentation;
        }

        public void AddNote(DocSection? section)
        {
            if (section == null) return;
            var note = new DocNote(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), string.Empty);
            section.Notes.Add(note);
            SaveNotes();
        }

        public void RemoveNote(DocSection? section, DocNote? note)
        {
            if (section == null || note == null) return;
            if (section.Notes.Remove(note))
            {
                SaveNotes();
            }
        }

        private static IEnumerable<DocSection> LoadDefaultSections()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("documentation.json", StringComparison.OrdinalIgnoreCase));

                if (resourceName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var json = reader.ReadToEnd();
                        var dto = JsonSerializer.Deserialize<List<SectionDto>>(json, SerializerOptions);
                        if (dto != null && dto.Count > 0)
                            return dto.Select(ToSection);
                    }
                }
            }
            catch
            {
                // ignore errors and fall back to placeholder content
            }

            return CreateFallbackSections();
        }

        private void LoadNotes()
        {
            if (!File.Exists(_notesPath))
                return;

            try
            {
                var json = File.ReadAllText(_notesPath);
                var noteSections = JsonSerializer.Deserialize<List<NotesSectionDto>>(json, SerializerOptions) ?? new();
                _suppressNoteSave = true;

                foreach (var entry in noteSections)
                {
                    if (string.IsNullOrWhiteSpace(entry.SectionId))
                        continue;
                    var section = _sections.FirstOrDefault(s => string.Equals(s.Id, entry.SectionId, StringComparison.OrdinalIgnoreCase));
                    if (section == null || entry.Notes == null)
                        continue;

                    foreach (var noteDto in entry.Notes)
                    {
                        var note = new DocNote(
                            noteDto.Id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                            noteDto.Body ?? string.Empty);
                        section.Notes.Add(note);
                    }
                }
            }
            catch
            {
                // ignore corrupted note files
            }
            finally
            {
                _suppressNoteSave = false;
            }
        }

        private void SaveNotes()
        {
            if (_suppressNoteSave)
                return;

            var payload = _sections
                .Where(s => s.Notes.Count > 0)
                .Select(s => new NotesSectionDto
                {
                    SectionId = s.Id,
                    Notes = s.Notes.Select(n => new NoteDto
                    {
                        Id = n.Id,
                        Body = n.Body
                    }).ToList()
                })
                .ToList();

            if (payload.Count == 0)
            {
                if (File.Exists(_notesPath))
                {
                    try { File.Delete(_notesPath); } catch { }
                }
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(_notesPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(payload, SerializerOptionsIndented);
                File.WriteAllText(_notesPath, json);
            }
            catch
            {
                // ignore save errors
            }
        }

        private void AttachNotesHandlers(DocSection section)
        {
            section.Notes.CollectionChanged += SectionNotes_CollectionChanged;
            foreach (var note in section.Notes)
            {
                note.PropertyChanged += Note_PropertyChanged;
            }
        }

        private void SectionNotes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (DocNote note in e.NewItems)
                    note.PropertyChanged += Note_PropertyChanged;
            }

            if (e.OldItems != null)
            {
                foreach (DocNote note in e.OldItems)
                    note.PropertyChanged -= Note_PropertyChanged;
            }

            if (!_suppressNoteSave)
                SaveNotes();
        }

        private void Note_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DocNote.Body) && !_suppressNoteSave)
                SaveNotes();
        }

        private static IEnumerable<DocSection> CreateFallbackSections()
        {
            return new[]
            {
                new DocSection(
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                    "Documentation",
                    "Documentation content is unavailable.",
                    new[]
                    {
                        new DocParagraph(
                            "Missing documentation",
                            "Ensure documentation.json is embedded with the application build.")
                    })
            };
        }

        private static DocSection ToSection(SectionDto dto)
        {
            var paragraphs = dto.Paragraphs?
                .Select(p => new DocParagraph(p.Heading ?? string.Empty, p.Body ?? string.Empty))
                .ToList() ?? new List<DocParagraph>();

            return new DocSection(
                string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : dto.Id!,
                dto.Title ?? string.Empty,
                dto.Summary ?? string.Empty,
                paragraphs);
        }

        private static string GetNotesPath()
        {
            var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
            return Path.Combine(baseDir, "documentation.notes.json");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class DocSection : INotifyPropertyChanged
    {
        private string _title;
        private string _summary;
        private readonly ObservableCollection<DocParagraph> _paragraphs;
        private readonly ObservableCollection<DocNote> _notes;

        public DocSection(string id, string title, string summary, IEnumerable<DocParagraph> paragraphs)
        {
            Id = id;
            _title = title;
            _summary = summary;
            _paragraphs = new ObservableCollection<DocParagraph>(paragraphs ?? Enumerable.Empty<DocParagraph>());
            _notes = new ObservableCollection<DocNote>();
        }

        public string Id { get; }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Summary
        {
            get => _summary;
            set
            {
                if (_summary != value)
                {
                    _summary = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<DocParagraph> Paragraphs => _paragraphs;

        public ObservableCollection<DocNote> Notes => _notes;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class DocParagraph
    {
        public DocParagraph(string heading, string body)
        {
            Heading = heading;
            Body = body;
        }

        public string Heading { get; }
        public string Body { get; }
    }

    internal sealed class DocNote : INotifyPropertyChanged
    {
        private string _body;

        public DocNote(string id, string body)
        {
            Id = id;
            _body = body;
        }

        public string Id { get; }

        public string Body
        {
            get => _body;
            set
            {
                if (_body != value)
                {
                    _body = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class SectionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("paragraphs")]
        public List<ParagraphDto>? Paragraphs { get; set; }
    }

    internal sealed class ParagraphDto
    {
        [JsonPropertyName("heading")]
        public string? Heading { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    internal sealed class NotesSectionDto
    {
        [JsonPropertyName("sectionId")]
        public string? SectionId { get; set; }

        [JsonPropertyName("notes")]
        public List<NoteDto>? Notes { get; set; }
    }

    internal sealed class NoteDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

}
