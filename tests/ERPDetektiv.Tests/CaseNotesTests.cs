using System.IO.Compression;
using System.Text;
using ERPDetektiv.Cli;
using ERPDetektiv.Contracts;
using ERPDetektiv.Core;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class CaseNotesTests
{
    [Fact]
    public void Empty_notes_produce_no_file()
    {
        // Eine leere notes.json suggeriert, es sei nach Kontext gefragt und nichts
        // geliefert worden.
        using var package = new TemporaryZip();
        DiagnosticExporter.Export(TestSnapshots.Snapshot(), package.Path, new ExportOptions(),
            notes: new CaseNotes());
        using var archive = ZipFile.OpenRead(package.Path);
        Assert.Null(archive.GetEntry("notes.json"));
        Assert.Equal(7, archive.Entries.Count);
    }

    [Fact]
    public void Notes_are_exported_and_listed_in_the_manifest()
    {
        using var package = new TemporaryZip();
        var notes = new CaseNotes("Auftragserfassung hängt beim Speichern", "seit dem Update am Montag",
            "nur Auftragserfassung", "Sage-Update auf 9.0.11.4");

        DiagnosticExporter.Export(TestSnapshots.Snapshot(), package.Path, new ExportOptions(), notes: notes);

        using var archive = ZipFile.OpenRead(package.Path);
        Assert.Equal(8, archive.Entries.Count);
        var content = Read(archive, "notes.json");
        Assert.Contains("Auftragserfassung hängt beim Speichern", content, StringComparison.Ordinal);
        Assert.Contains("seit dem Update am Montag", content, StringComparison.Ordinal);
        // Die Pruefsumme muss die Notizen einschliessen, sonst ist das Manifest unvollstaendig.
        Assert.Contains("notes.json", Read(archive, "manifest.json"), StringComparison.Ordinal);
        Assert.Contains("Fallnotizen", Read(archive, "summary.html"), StringComparison.Ordinal);
        Assert.Contains("nur Auftragserfassung", Read(archive, "summary.html"), StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_are_scrubbed_and_pseudonymized_like_the_rest_of_the_package()
    {
        // Wer den Servernamen ins Symptomfeld schreibt, darf ihn nicht damit exportieren.
        var snapshot = TestSnapshots.Snapshot();
        var notes = new CaseNotes("HOST-A antwortet langsam", AdditionalNotes: "Verbindung mit Password=geheim123");

        var redacted = Redactor.RedactNotes(notes, snapshot, pseudonymize: true);

        Assert.DoesNotContain("HOST-A", redacted.Symptom!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("antwortet langsam", redacted.Symptom!, StringComparison.Ordinal);
        Assert.DoesNotContain("geheim123", redacted.AdditionalNotes!, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_stay_readable_without_pseudonymization()
    {
        var redacted = Redactor.RedactNotes(new CaseNotes("HOST-A ist langsam"), TestSnapshots.Snapshot(),
            pseudonymize: false);
        Assert.Equal("HOST-A ist langsam", redacted.Symptom);
    }

    [Fact]
    public void Html_escapes_note_text()
    {
        var html = HtmlSummary.Create(TestSnapshots.Snapshot(), new CaseNotes("<script>alert(1)</script>"));
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_reads_notes_from_a_file_and_from_the_symptom_option()
    {
        var file = Path.Combine(Path.GetTempPath(), $"erpdetektiv-notes-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file,
                """{"symptom":"aus Datei","observedSince":"gestern","affectedArea":"Lager"}""", Encoding.UTF8);

            var fromFile = CliOptions.Parse(["--notes-file", file]);
            Assert.Equal("aus Datei", fromFile.Notes!.Symptom);
            Assert.Equal("Lager", fromFile.Notes.AffectedArea);

            // --symptom ergaenzt die Datei und hat Vorrang.
            var combined = CliOptions.Parse(["--notes-file", file, "--symptom", "direkt angegeben"]);
            Assert.Equal("direkt angegeben", combined.Notes!.Symptom);
            Assert.Equal("gestern", combined.Notes.ObservedSince);

            Assert.Null(CliOptions.Parse([]).Notes);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void Cli_rejects_an_unreadable_notes_file()
    {
        Assert.Throws<ArgumentException>(() =>
            CliOptions.Parse(["--notes-file", Path.Combine(Path.GetTempPath(), $"fehlt-{Guid.NewGuid():N}.json")]));
    }

    private static string Read(ZipArchive archive, string entry)
    {
        using var reader = new StreamReader(archive.GetEntry(entry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class TemporaryZip : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"erpdetektiv-{Guid.NewGuid():N}.zip");

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
