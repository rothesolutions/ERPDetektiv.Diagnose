using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using ERPDetektiv.Application;
using ERPDetektiv.Core;
using Microsoft.Win32;

namespace ERPDetektiv.Gui;

public partial class MainWindow : System.Windows.Window
{
    private bool _connectionVerified;
    private CancellationTokenSource? _statusReset;
    private CancellationTokenSource? _operation;

    public MainWindow()
    {
        InitializeComponent();
        UpdateAuthenticationFields();
        SetStatus("Bereit. Server eintragen und Verbindung prüfen.", StatusTone.Neutral);
        Loaded += SuggestSqlServerFromRegistry;
    }

    /// <summary>Belegt das Serverfeld mit dem SQL-Server der Sage-Installation vor.</summary>
    /// <remarks>
    /// Nach dem Anzeigen und nur in ein leeres Feld: Der Vorschlag darf weder den Start
    /// verzoegern noch eine Eingabe ueberschreiben. Schlaegt die Registry-Abfrage fehl,
    /// bleibt es beim bisherigen Verhalten – ein fehlender Vorschlag ist kein Fehler,
    /// ueber den die Anwenderin oder der Anwender etwas erfahren muss.
    /// </remarks>
    private async void SuggestSqlServerFromRegistry(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= SuggestSqlServerFromRegistry;
        string? suggestion;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            suggestion = await DiagnosticComposition.SuggestSqlServerAsync(timeout.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { return; }

        if (string.IsNullOrWhiteSpace(suggestion) || !string.IsNullOrWhiteSpace(ServerName.Text)) return;
        ServerName.Text = suggestion;
        SetStatus($"Server aus der Sage-Registry vorbelegt: {suggestion}. Verbindung prüfen.", StatusTone.Neutral);
    }

    private void Authentication_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateAuthenticationFields();
        InvalidateConnection();
    }

    private void ConnectionInput_Changed(object sender, System.Windows.RoutedEventArgs e) => InvalidateConnection();
    private void ConnectionOption_Changed(object sender, System.Windows.RoutedEventArgs e) => InvalidateConnection();

    private void UpdateAuthenticationFields()
    {
        if (UserLabel is null || UserName is null || PasswordLabel is null || Password is null) return;
        var visibility = Authentication.SelectedIndex == 1
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        UserLabel.Visibility = visibility;
        UserName.Visibility = visibility;
        PasswordLabel.Visibility = visibility;
        Password.Visibility = visibility;
    }

    private async void TestButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        InvalidateConnection();
        using var operation = StartOperation();
        SetStatus("Prüfe SQL-Verbindung und Mindestzugriffe …", StatusTone.Info);
        try
        {
            var collector = DiagnosticComposition.CreateSqlCollector(TryBuildConnectionString(), ServerName.Text.Trim(),
                "master", includeSelectedDatabaseDetails: false)
                ?? throw new InvalidOperationException("Der SQL-Server muss angegeben werden.");
            // Der Reader arbeitet blockierend; ohne Task.Run friert die Oberflaeche ein.
            var result = await Task.Run(() => collector.CollectAsync(operation.Token), operation.Token);
            var snapshot = result.Value!;
            var databases = snapshot.Databases
                .Where(database => database.Name is not ("master" or "model" or "msdb" or "tempdb"))
                .Select(database => database.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            DatabaseName.ItemsSource = databases;
            // Bewusst keine Vorauswahl: Welche Datenbank untersucht wird, entscheidet die
            // Anwenderin oder der Anwender. Der erste Eintrag der Liste waere eine
            // beliebige Datenbank, und eine Diagnose der falschen faellt spaeter kaum auf.
            DatabaseName.SelectedItem = null;
            DatabaseName.IsEnabled = databases.Count > 0;
            _connectionVerified = true;
            var databaseHint = databases.Count == 0
                ? " Keine Benutzerdatenbank gefunden – die Diagnose läuft ohne datenbankspezifische Details."
                : " Jetzt die zu untersuchende Datenbank wählen.";
            SetStatus(
                $"Verbindung erfolgreich. SQL Server: {snapshot.Version} ({snapshot.Edition}); {snapshot.Databases.Count} Datenbanken erkannt.{databaseHint}",
                StatusTone.Success, resetAfterDelay: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Verbindungstest abgebrochen.", StatusTone.Neutral);
        }
        catch (Exception exception)
        {
            SetStatus(DescribeConnectionFailure(exception), StatusTone.Error);
        }
        finally { EndOperation(); }
    }

    private async void RunButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Filter = "Diagnosepaket (*.zip)|*.zip",
            FileName =
                $"erpdetektiv-diagnostic-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip"
        };
        if (picker.ShowDialog(this) != true) return;
        using var operation = StartOperation();
        try
        {
            var runner = CreateRunner();
            var progress = new Progress<string>(message => SetStatus(message, StatusTone.Info));
            var snapshot = await Task.Run(() => runner.RunAsync(progress, operation.Token), operation.Token);
            DiagnosticExporter.Export(snapshot, picker.FileName,
                new ExportOptions(PseudonymizeIdentifiers: Pseudonymize.IsChecked == true), notes: CollectNotes());
            Findings.ItemsSource = snapshot.Findings.OrderByDescending(finding => finding.Severity).ToList();
            var incomplete = snapshot.CollectionStatus
                .Where(status => status.State is Contracts.CollectorState.Failed or Contracts.CollectorState.Partial)
                .Select(status => status.CollectorId).ToList();
            var hint = incomplete.Count == 0
                ? string.Empty
                : $" Unvollständig erfasst: {string.Join(", ", incomplete)} – Details siehe Erfassungsstatus im Bericht.";
            SetStatus(
                $"Diagnosepaket erstellt: {picker.FileName} – {snapshot.Findings.Count} Findings. Der ZIP-Export enthält auch einen lesbaren HTML-Bericht.{hint}",
                incomplete.Count == 0 ? StatusTone.Success : StatusTone.Info, resetAfterDelay: incomplete.Count == 0);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Diagnose abgebrochen; es wurde kein Paket geschrieben.", StatusTone.Neutral);
        }
        catch (Exception exception)
        {
            SetStatus($"Die Diagnose konnte nicht erstellt werden: {exception.Message}", StatusTone.Error);
        }
        finally { EndOperation(); }
    }

    private void CancelButton_Click(object sender, System.Windows.RoutedEventArgs e) => _operation?.Cancel();

    private async void PreflightButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        using var operation = StartOperation();
        SetStatus("Vorabcheck läuft …", StatusTone.Info);
        try
        {
            var progress = new Progress<string>(message => SetStatus(message, StatusTone.Info));
            var preflight = DiagnosticComposition.CreatePreflightRunner(
                Path.Combine(Path.GetTempPath(), "erpdetektiv-probe.zip"), TryBuildConnectionString(),
                DatabaseName.SelectedItem as string);
            var report = await Task.Run(() => preflight.RunAsync(progress, operation.Token),
                operation.Token);
            // Die Ergebnisliste nutzt dieselbe Ansicht wie die Findings; ein eigenes Raster
            // waere fuer vier Spalten dieselbe Information in doppelter Ausfuehrung.
            Findings.ItemsSource = report.Results
                .Select(result => new { Severity = Describe(result.State), result.Name, Description = result.Message })
                .ToList();
            SetStatus(report.Summary(),
                report.HasFailures ? StatusTone.Error : report.HasWarnings ? StatusTone.Info : StatusTone.Success,
                resetAfterDelay: !report.HasFailures && !report.HasWarnings);
        }
        catch (OperationCanceledException) { SetStatus("Vorabcheck abgebrochen.", StatusTone.Neutral); }
        catch (Exception exception) { SetStatus($"Vorabcheck fehlgeschlagen: {exception.Message}", StatusTone.Error); }
        finally { EndOperation(); }
    }

    private static string Describe(Contracts.PreflightState state) => state switch
    {
        Contracts.PreflightState.Ok => "OK",
        Contracts.PreflightState.Warning => "Warnung",
        Contracts.PreflightState.Failed => "Fehler",
        _ => "Übersprungen"
    };

    /// <summary>Connection String fuer den Vorabcheck, sofern die Eingaben ausreichen.</summary>
    private string? TryBuildConnectionString()
    {
        try
        {
            return CreateConnectionStringBuilder(DatabaseName.SelectedItem as string).ConnectionString;
        }
        catch (InvalidOperationException)
        {
            // Unvollstaendige Eingaben sind im Vorabcheck kein Fehler; die SQL-Sonde
            // meldet dann "übersprungen".
            return null;
        }
    }

    /// <summary>Fallnotizen aus der Oberflaeche; leere Felder bleiben leer.</summary>
    private Contracts.CaseNotes? CollectNotes()
    {
        var notes = new Contracts.CaseNotes(
            Blank(NoteSymptom.Text), Blank(NoteObservedSince.Text), Blank(NoteAffectedArea.Text),
            Blank(NoteRecentChanges.Text), Blank(NoteAdditional.Text));
        return notes.IsEmpty ? null : notes;

        static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private DiagnosticRunner CreateRunner()
    {
        var database = DatabaseName.SelectedItem as string;
        var connection = CreateConnectionStringBuilder(database).ConnectionString;
        return DiagnosticComposition.CreateRunner(connection, ServerName.Text.Trim(), database);
    }

    private DbConnectionStringBuilder CreateConnectionStringBuilder(string? database)
    {
        if (string.IsNullOrWhiteSpace(ServerName.Text))
            throw new InvalidOperationException("Der SQL-Server muss angegeben werden.");
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = ServerName.Text.Trim(),
            ["Application Name"] = "ERPDetektiv Diagnose",
            ["Encrypt"] = Encrypt.IsChecked == true,
            ["TrustServerCertificate"] = TrustCertificate.IsChecked == true
        };
        if (!string.IsNullOrWhiteSpace(database)) builder["Initial Catalog"] = database;
        if (Authentication.SelectedIndex == 0) builder["Integrated Security"] = true;
        else
        {
            if (string.IsNullOrWhiteSpace(UserName.Text) || string.IsNullOrEmpty(Password.Password))
                throw new InvalidOperationException("Benutzer und Kennwort müssen angegeben werden.");
            builder["User ID"] = UserName.Text.Trim();
            builder["Password"] = Password.Password;
        }

        return builder;
    }

    /// <summary>Uebersetzt Verbindungsfehler in einen Hinweis, mit dem weitergearbeitet werden kann.</summary>
    private string DescribeConnectionFailure(Exception exception)
    {
        if (!IsCertificateTrustProblem(exception) || TrustCertificate.IsChecked == true)
            return $"Verbindung nicht möglich: {exception.Message}";
        return "Verbindung nicht möglich: Das Serverzertifikat konnte nicht überprüft werden. " +
               "SQL Server verwendet ab Werk ein selbstsigniertes Zertifikat. Prüfe, ob der Servername exakt zum " +
               "Zertifikat passt. Ist der Server vertrauenswürdig und das Netz kontrolliert, kannst du " +
               "\"Serverzertifikat vertrauen\" setzen – die Verbindung bleibt dann verschlüsselt, das Zertifikat " +
               "wird aber nicht mehr geprüft.";
    }

    private static bool IsCertificateTrustProblem(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException) return true;
            if (current.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Zertifikat", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private CancellationTokenSource StartOperation()
    {
        _operation = new CancellationTokenSource();
        Progress.Visibility = System.Windows.Visibility.Visible;
        RunButton.IsEnabled = false;
        TestButton.IsEnabled = false;
        PreflightButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        return _operation;
    }

    private void EndOperation()
    {
        _operation = null;
        Progress.Visibility = System.Windows.Visibility.Collapsed;
        CancelButton.IsEnabled = false;
        TestButton.IsEnabled = true;
        PreflightButton.IsEnabled = true;
        UpdateRunAvailability();
    }

    private void InvalidateConnection()
    {
        _connectionVerified = false;
        if (DatabaseName is not null)
        {
            // Die Liste gehoert zur geprueften Verbindung. Bleibt sie nach einer Aenderung
            // am Server stehen, koennte eine alte Auswahl die neue Pflichtwahl erfuellen.
            DatabaseName.ItemsSource = null;
            DatabaseName.IsEnabled = false;
        }

        if (RunButton is not null) RunButton.IsEnabled = false;
    }

    private void Database_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateRunAvailability();

    /// <summary>Der Export braucht eine ausdruecklich gewaehlte Datenbank.</summary>
    /// <remarks>
    /// Ausgenommen ist die Instanz ohne Benutzerdatenbanken: Sie ist weiterhin
    /// diagnostizierbar, nur eben ohne datenbankspezifische Details. Eine Pflichtwahl aus
    /// einer leeren Liste waere eine Sackgasse.
    /// </remarks>
    private bool DatabaseSelectionComplete =>
        DatabaseName.ItemsSource is not ICollection<string> { Count: > 0 } || DatabaseName.SelectedItem is string;

    private void UpdateRunAvailability()
    {
        if (RunButton is null || DatabaseName is null) return;
        RunButton.IsEnabled = _connectionVerified && DatabaseSelectionComplete;
    }

    private void SetStatus(string message, StatusTone tone, bool resetAfterDelay = false)
    {
        _statusReset?.Cancel();
        Result.Text = message;
        (StatusPanel.Background, Result.Foreground) = tone switch
        {
            StatusTone.Success => (new SolidColorBrush(Color.FromRgb(220, 252, 231)),
                new SolidColorBrush(Color.FromRgb(22, 101, 52))),
            StatusTone.Error => (new SolidColorBrush(Color.FromRgb(254, 226, 226)),
                new SolidColorBrush(Color.FromRgb(153, 27, 27))),
            StatusTone.Info => (new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                new SolidColorBrush(Color.FromRgb(30, 64, 175))),
            _ => (Brushes.Transparent, Brushes.DimGray)
        };
        if (!resetAfterDelay) return;
        _statusReset = new CancellationTokenSource();
        _ = ResetStatusColorAsync(_statusReset.Token);
    }

    private async Task ResetStatusColorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                StatusPanel.Background = Brushes.Transparent;
                Result.Foreground = Brushes.DimGray;
            }
        }
        catch (OperationCanceledException) { }
    }

    private enum StatusTone { Neutral, Info, Success, Error }
}
