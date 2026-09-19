# GUI-Workflow

## Zweck

Die WPF-Anwendung führt einen lokalen, einmaligen Diagnoseexport aus. Sie
speichert weder Zugangsdaten noch baut sie eine Dauerverbindung auf.

## Ablauf

1. SQL Server und Anmeldeart eintragen. Windows-Authentifizierung ist der
   Standard; bei SQL-Authentifizierung erscheinen Benutzername und Kennwort.
   Läuft die Anwendung auf einem Sage-Server, ist das Serverfeld beim Start
   bereits aus der Sage-Registry vorbelegt; der Vorschlag lässt sich
   überschreiben. Ohne lesbare Registry bleibt das Feld leer.
2. **Verbindung prüfen** auswählen. Der Test verbindet sich mit `master` und
   liest ausschließlich Servermetadaten sowie die Liste der erreichbaren
   Datenbanken. Er führt keine Query-Store- oder Log-Space-Abfragen aus.
3. Nach Erfolg die Datenbank im nun freigeschalteten Dropdown wählen. Die
   Auswahl ist Pflicht und bewusst nicht vorbelegt: Der erste Eintrag der Liste
   wäre eine beliebige Datenbank, und eine Diagnose der falschen fällt später
   kaum auf. **Diagnose & Export** bleibt bis zur Wahl gesperrt. Ausgenommen ist
   eine Instanz ohne Benutzerdatenbanken – sie bleibt diagnostizierbar, nur ohne
   datenbankspezifische Details.

   Eine Änderung am Server verwirft die Liste samt Auswahl. Sonst könnte eine
   Wahl aus der vorherigen Verbindung die Pflichtwahl erfüllen.
4. **Diagnose & Export** auswählen und den Speicherort des ZIP-Pakets wählen.
   Erst dann erfasst das Tool die datenbankspezifischen Fakten der gewählten
   Datenbank, etwa Query Store und Log-Space.

 Vor Schritt 4 lohnt sich **Vorabcheck**: Er prüft in wenigen Sekunden
 Exportpfad, Windows-Inventur, SQL-Erreichbarkeit samt Mindestberechtigungen und
 Sage-Erkennung, ohne ein Paket zu schreiben. Das Ergebnis erscheint in der
 Ergebnisliste. Auf einem fremden Kundensystem ist damit sofort klar, welche
 Teile des Pakets vollständig werden - statt es hinterher am Erfassungsstatus
 abzulesen.

Änderungen an Server, Anmeldeart, Benutzer, Kennwort oder TLS-Optionen machen
den vorherigen Verbindungstest ungültig. Eine erneute Prüfung ist dann nötig,
bevor ein Export gestartet werden kann.

Systemdatenbanken (`master`, `model`, `msdb`, `tempdb`) erscheinen weiterhin
im Export und HTML-Bericht, sind aber nicht als Zieldatenbank in der GUI
auswählbar.

## Rückmeldung

Die Statuszeile unterhalb der Findings zeigt die aktuelle Aktion und das
Ergebnis. Erfolg wird für drei Sekunden dezent grün, Fehler rot und laufende
Aktionen blau hinterlegt. Danach bleibt die Meldung in neutraler Darstellung
sichtbar.

## Sicherheit

Kennwörter werden ausschließlich für die aktive Verbindung im Speicher
gehalten. Sie erscheinen nicht im Diagnosepaket, im HTML-Bericht oder in
Protokollmeldungen. Optional können Host- und Servernamen vor dem Export
pseudonymisiert werden; Datenbanknamen bleiben für die fachliche Einordnung
lesbar.

## Fallnotizen

Im Reiter **Fallnotizen** lassen sich freiwillige Angaben zum Fall eintragen:
beobachtetes Symptom, seit wann, betroffener Bereich, letzte Änderungen und
weitere Hinweise. Sie landen als `notes.json` im Paket und als erster Abschnitt
im HTML-Bericht – vor den Messwerten, weil sie sagen, wonach zu suchen ist.

Die Angaben sind optional; ohne Eintrag entsteht keine `notes.json`. Sie sind
ausdrücklich Beobachtungen, keine gemessenen Fakten, und im Bericht auch so
gekennzeichnet.

Der Text durchläuft dieselbe Secret-Filterung und Pseudonymisierung wie der
übrige Export: Wer den Servernamen in das Symptomfeld schreibt, findet dort
dieselbe Kennung wie im restlichen Paket. Das ersetzt keine Sorgfalt beim
Formulieren – Kunden- oder Personennamen gehören nicht hinein.
