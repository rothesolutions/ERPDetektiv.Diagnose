# Erfasste Daten und Berechtigungen

## Pseudonymisierung: was geschützt wird und was bewusst lesbar bleibt

Diese Festlegung ist eine Produktentscheidung, keine technische Nebensache. Sie
gilt für alle Collector und Checks; neue Datenpunkte werden hier eingeordnet.

**Geschützt** (werden bei `--pseudonymize` beziehungsweise aktivierter
Pseudonymisierung in der Oberfläche durch stabile Kennungen ersetzt):

- Hostname des untersuchten Systems
- Name und Instanz des SQL Servers, einschließlich des Hostanteils in Angaben
  wie `tcp:HOST\INSTANZ,1433`
- Hostanteil von SData-Endpunktadressen; Schema, Port und Pfad bleiben erhalten
- Namen der Sage-Server aus der Registry: Application Server, Blobstorage,
  Application Gateway, Stationen und der Sage-Server aus `Server\Name`. Der
  `ServerName` einer Datenquelle wird wie ein SQL-Server behandelt, also
  einschließlich Instanzanteil

**Bewusst lesbar** (werden nicht ersetzt):

- Datenbanknamen
- Windows-Dienstnamen
- Sage-Komponenten- und Installationsverzeichnisse
- Laufwerksbuchstaben und Dateinamen

Der Grund für die zweite Liste: Aus diesen Namen lässt sich das technische
Umfeld ableiten, und genau das ist für eine Diagnose wertvoll. Eine Datenbank
`DWData` weist beispielsweise auf ein DocuWare-Umfeld hin – eine Information,
die eine Analyse maßgeblich beeinflussen kann, ohne dass jemand in die Daten
selbst sehen muss. Eine Pseudonymisierung, die auch diese Namen erfasst, würde
das Diagnosepaket entwerten.

Die Ersetzung greift im **gesamten** Paket, also auch in Finding-Freitexten,
Evidence-Werten, Collector-Meldungen und im HTML-Bericht. Verantwortlich ist
`IdentifierRegistry`; Checks müssen dafür nichts Eigenes tun.

### Grenzen der Pseudonymisierung

- Die Kennungen sind ein ungesalzener SHA-256-Hash über den Originalwert. Sie
  sind damit **über Diagnosepakete hinweg vergleichbar** – Voraussetzung für
  spätere Vorher-/Nachher-Vergleiche –, aber **nicht gegen gezieltes
  Durchprobieren bekannter Namen geschützt**. Wer einen Hostnamen vermutet,
  kann ihn durch Nachrechnen bestätigen. Für „den Namen nicht beiläufig aus
  der Hand geben" reicht das; für „nicht rückrechenbar" nicht.
- Instanznamen bleiben über die Dienstliste sichtbar: Ein Dienst
  `MSSQL$SAGE100` wird nicht umgeschrieben, weil Dienstnamen zur lesbaren
  Kategorie gehören.
- Zugangsdaten werden unabhängig von dieser Einstellung nie exportiert.
  Erkannte Muster wie `password=`, `pwd=`, `clientSecret=`, `apiKey=` und
  `accessToken=` werden in Freitexten zusätzlich unkenntlich gemacht.

## Fallnotizen

Die Fallnotizen sind freiwillige Angaben der Anwenderin oder des Anwenders und
bewusst vom automatischen Collector getrennt: Symptom, Zeitpunkt, betroffener
Bereich, letzte Änderungen, weitere Hinweise. Sie entstehen nur durch Eingabe;
ohne Eintrag enthält das Paket keine `notes.json`.

Der Text ist frei formuliert und durchläuft dieselbe Secret-Filterung und
Pseudonymisierung wie der übrige Export. Das ersetzt keine Sorgfalt: Wer
Kundennamen oder personenbezogene Daten einträgt, exportiert sie auch. Der
HTML-Bericht kennzeichnet die Angaben als Beobachtungen, nicht als Messwerte.

## Vorabcheck

Der Vorabcheck führt keine Erfassung durch und schreibt kein Paket. Er öffnet
eine SQL-Verbindung, prüft mit je einer minimalen Abfrage `sys.dm_os_sys_info`,
`sys.databases`, `msdb.dbo.backupset` und die gewählte Datenbank, liest die
Windows-Dienstliste, sucht die Sage-Installation und legt eine Testdatei im
Zielverzeichnis an. Die Testdatei wird sofort wieder gelöscht.

Die Sage-Laufzeitabfrage über die Administrationsbibliotheken bleibt im
Vorabcheck bewusst aus; geprüft wird nur, ob Application Server und die
nötige 32-Bit-PowerShell vorhanden sind.

## Windows

Der Windows-Collector liest Betriebssystem, Hostname, Architektur, Prozessor­
modell, Anzahl logischer Kerne, physischen Host-RAM, Zeitzone sowie Größe und
freien Platz lokaler bereiter Laufwerke. Zusätzlich erfasst er ausschließlich
SQL- und Sage-nahe Windows-Dienste mit Status und Starttyp, den aktiven
Energieplan samt GUID und vorhandene .NET-Runtimes.

Das funktioniert ohne erhöhte Rechte. Eine nicht verfügbare Inventur wird als
Teilstatus dokumentiert, nicht als Programmfehler.

Der Energieplan wird über die GUID ausgewertet, nicht über den übersetzten
Anzeigenamen. Der physische RAM stammt aus `Win32_ComputerSystem`; er ist
bewusst nicht mit der Speichergrenze des eigenen Prozesses zu verwechseln.

## SQL Server

Der Reader benötigt als Mindestziel `VIEW SERVER STATE` sowie
`VIEW DATABASE STATE` in den ausgewählten Datenbanken; `sysadmin` ist keine
Voraussetzung.

Zusätzlich je Datenbank die mittlere Lese- und Schreibzeit je Zugriff aus
`sys.dm_io_virtual_file_stats` sowie instanzweit Page Life Expectancy, Größe des
Bufferpools und wartende Memory Grants aus `sys.dm_os_performance_counters`. Die
Zähler sind kumulativ seit Instanzstart; ein einzelner Snapshot genügt, es braucht
keine Zeitreihe.

Erfasst werden Version, Edition, Startzeitpunkt, CPU-Topologie,
Virtualisierungsart, Host-Speicher, Always-On-Status sowie die
Konfigurationswerte `max server memory`, `min server memory`, MAXDOP und
Cost Threshold for Parallelism. Je Datenbank kommen Status, Größe, Recovery
Model, Compatibility Level, logische Dateien mit Typ, Größe und
Autogrowth-Konfiguration sowie der letzte Full-Backup-Zeitpunkt hinzu.
Physische Datenbankpfade werden nicht exportiert.

Jeder Bereich wird einzeln abgefragt und einzeln gekapselt. Fehlt eine
Berechtigung, entfällt genau dieser Bereich; der Collector meldet `Partial`
und benennt die nicht gelesenen Bereiche im Erfassungsstatus. Ein
unvollständiger Snapshot ist damit erkennbar und trotzdem brauchbar.

Serverzeitstempel werden mit dem Zeitzonenversatz der Instanz gespeichert.
Ohne ihn wäre ein Backup-Zeitpunkt mit der Zeitzone des auswertenden Rechners
interpretiert worden.

Query Store und Log-Space werden erst bei der Diagnose für die ausgewählte
Datenbank abgefragt und je Funktion als `Available`, `NotSupported`,
`AccessDenied`, `NotAvailable` oder `Failed` ausgewiesen. Der GUI-Verbindungs­
test erfasst ausschließlich serverweite Metadaten und die Liste erreichbarer
Datenbanken; ein deaktivierter Query Store verhindert ihn nicht.

### Verschlüsselung der Verbindung

Die Verbindung ist standardmäßig verschlüsselt, und das Serverzertifikat wird
standardmäßig **geprüft**. Da SQL Server ab Werk ein selbstsigniertes
Zertifikat verwendet, kann die erste Verbindung daran scheitern. Die
Oberfläche erkennt diesen Fall und erklärt ihn; „Serverzertifikat vertrauen"
bleibt eine bewusste Entscheidung der Anwenderin oder des Anwenders.

## Sage 100

Den Installationsort nennt die Registry; die Verzeichnissuche errät ihn. Die
Reihenfolge ist deshalb: `ERPDETEKTIV_SAGE_ROOT`, dann der Eintrag
`Office Line\<Version>\Root` aus der Registry, dann die Standardpfade unter
„Programme" und „Programme (x86)". Die Umgebungsvariable bleibt damit die
ausdrückliche Übersteuerung – wer sie setzt, meint sie auch –, ist aber kein
Regelweg mehr.

Ein Registry-Eintrag wird nur übernommen, wenn das Verzeichnis existiert. Der
Eintrag überlebt eine Deinstallation; ein gemeldeter Pfad, den es nicht mehr
gibt, wäre schlechter als ein ehrliches „nicht erkannt". Bei mehreren
Installationen in den durchsuchten Pfaden wird die höchste Version gewählt,
nicht der zuletzt geänderte Ordner.

Die Komponentenliste vereinigt beide Quellen, weil beide für sich unvollständig
sind: Die Registry führt nur Produkte mit eigenem Versionsknoten und dortigem
`Root`, die Verzeichnissuche nur, was unterhalb desselben Sage-Ordners liegt.

Die Liste ist eine Bestandsaufnahme des Umfelds, keine Bewertung. Zusatzprodukte
von Business-Partnern erscheinen darin, wenn sie im Sage-Verzeichnis liegen –
sie werden weder ausgewertet noch von einem Check betrachtet.

Die Oberfläche belegt das Feld „Server" beim Start mit dem SQL-Server der
Sage-Installation vor – aus der Datenquelle `OLGlobal`, ersatzweise aus dem
Server, auf dem die meisten Datenquellen liegen. Der Vorschlag füllt nur ein
leeres Feld, überschreibt keine Eingabe und bleibt änderbar. Ist die Registry
nicht lesbar, bleibt das Feld leer.

Gelesen werden ausschließlich der Installationspfad, die Namen der
Installationskomponenten und die `Buildinfo.xml` mit der maßgeblichen internen
`FixVersion`.

Ist ein lokaler Sage Application Server installiert, nutzt die Diagnose dessen
32-Bit-Administrationsbibliotheken über einen kurzlebigen, lesenden
PowerShell-Adapter mit harter Zeitgrenze. Erfasst wird nur eine feste Whitelist
technischer Werte: Isolation, Pool-Größen, Timeouts, Logging, Anzahl, Zustand
und Alter der Service-Domains, Kontextwechsel, Call-Zähler und Working Set
sowie Namen und Operationen zusätzlicher SOAP-Services. Der Adapter liest keine
Konfigurationsrohdaten in den Export.

Service-Domains werden zusätzlich einzeln mit ihrer Kennung erfasst. Die reine
Summe über alle Domains ist nicht auswertbar, weil der Pool Domains erzeugt und
verwirft: Die Summe fällt dann, ohne dass die Last gesunken wäre.

Connection Strings, `clientSecret`, Zertifikat-Thumbprints, Benutzer,
Mandanten, Datenbanknamen aus Service-Domains und Geschäftsdaten sind kein
Erfassungsziel. Fehlen Sage-Bibliotheken, Berechtigungen oder die erforderliche
32-Bit-PowerShell, wird nur dieser Teil als nicht verfügbar dokumentiert – mit
eigenem Eintrag `sage100.application-server` im Erfassungsstatus. Die übrige
Diagnose läuft weiter.

### Registry

Gelesen wird ausschließlich `HKLM\SOFTWARE\WOW6432Node\Sage` — Sage 100 ist eine
32-Bit-Anwendung, unter `HKLM\SOFTWARE\Sage` steht nichts. Der Versionsknoten
(`9.0`) wird aufgezählt, nicht angenommen; ebenso alle Unterschlüssel mit
variabler Menge: SData-Bindings, Blobstorage-Endpunkte und die Applikationen
unter `NamedUsers`.

Die Inventardaten stehen **nur auf dem Sage-Server**. Auf einem zusätzlichen
Application Server fehlen `Admin\Datasources`, `Application Server\<HOST>`,
`Security\*` und `Connectivity Server` vollständig. `Office Line\<Version>\Server`
zeigt auf allen Stationen auf den Sage-Server; stimmt der Wert nicht mit dem
eigenen Hostnamen überein, meldet der Collector das als Teilerfassung und benennt
den Server, statt „nicht gefunden" zu behaupten.

Erfasst wird eine feste Whitelist:

- Installationsverzeichnisse der Produkte (`Application Server`,
  `BlobStorage Server`, `Office Line`) aus dem jeweiligen `Root`
- Application Server je Host mit `State` und `Capability` (3 = Master und
  Lastverteilung, 1 = Teilnehmer der Lastverteilung, 0 = Einzelbetrieb) samt
  SData-Endpunkten mit Binding, Adresse und Authentifizierungsart
- Blobstorage-Registrierungen je Host mit `Status`, `Activated` und Endpunkten
- Application Gateway je Host mit `State` und Adresse — **ohne** `ApiKey`
- Stationen aus `Connectivity Server` mit Adresse und Zustand
- Datenquellen mit Name, Server, Datenbank, Applikationen und hinterlegter
  `ServerVersion`
- Anzahl belegter benannter Benutzer je Applikation
- die Einzelwerte `LargeAddressAwareClient`, `PreloadUi`, `APLockTimer`,
  `MetaDataDataRevision`, `CentralAddInDirectory`, `MacroDebugger\IsEnabled`
  und `Security\Aufgaben-Center\VBScript`

Nicht erfasst werden Mandantennamen — von den `Mandators` geht ausschließlich
die **Anzahl** in den Export. Ebenso wenig die Namen benannter Benutzer; auch
hier bleibt nur die Anzahl je Applikation. Beides sind Geschäfts- und
Personendaten und fällt unter dieselbe Abgrenzung wie die Service-Domains.

Nicht erfasst werden außerdem die Geheimnisse, die in diesem Teilbaum liegen und
die der bestehende Secret-Filter nicht greifen würde, weil sie als eigene Werte
ohne `password=`-Muster gespeichert sind: `License\Key` samt Kundenname,
`LogiSoft\Password` und `Master`, `Options\GlobalAccountUID`, `GlobalAccountPWD`,
`InstanceID` und `MetaDataInfo`, `Security\OTFKeys`, `Security\Authentication`,
`Security\Computers`, sämtliche `#secVal#`-Werte sowie der `ApiKey` des
Application Gateway. Von den `DatasourceKeys` gehen nur die **Namen** in den
Export, nicht die Werte — sie dienen dem Abgleich gegen die registrierten
Datenquellen.

Der `LogiSoft`-Schlüssel wird nur auf Anwesenheit geprüft. Er gehört zum
Aufgaben-Center, dessen Anmeldedaten dort historisch abgelegt sind; kein Wert
daraus wird gelesen.

## Findings

Die ersten Findings bleiben absichtlich konservativ. Nicht laufende Sage-Dienste
werden nur gemeldet, wenn sie auf automatischen Start gesetzt sind – Dienste mit
Starttyp `Manual` oder `Disabled` sind planmäßig gestoppt. Datenbanken, die
nicht online sind, werden von Backup- und Autogrowth-Regeln ausgenommen.
Zusätzliche SOAP-Services und SData-Endpunkte mit Binding `HttpsNone` sind
Informationen, keine pauschalen Fehlkonfigurationen.

## Woher die Schwellenwerte stammen

Die Findings zu Zugriffszeit, Page Life Expectancy und wartenden Memory Grants
sind aus einer Zehnstundenmessung über vier Server hervorgegangen. Ihre
Grenzwerte stammen aber ausdrücklich **nicht** aus dieser Messung, sondern aus
etablierter Praxis – unter 10 ms gut und über 20 ms auffällig gilt unabhängig von
Systemgröße und Last.

Das ist die Bedingung, an der andere Kandidaten gescheitert sind: Regeln zum
CXPACKET-Anteil, zum Signal-Wait-Anteil und zur bloßen Anwesenheit von
Blockierungen hätten auf einem als gut eingestellt betrachteten Referenzsystem
angeschlagen und sind deshalb ausgeschieden. Ein Schwellenwert taugt nur, wenn er
eine Bezugsgröße außerhalb des gemessenen Systems hat.

