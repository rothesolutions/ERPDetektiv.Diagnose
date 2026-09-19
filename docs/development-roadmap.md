# Entwicklungsfahrplan

## Versionsentscheidung

Die erste öffentliche Veröffentlichung ist **v0.2.0** als GitHub-Pre-release.
Das Werkzeug ist nutzbar; Umfang, Paketformat und Checks sollen sich jetzt an
echten Fällen bewähren. Die 0.x-Version macht diese Lernphase transparent.

**v1.0.0** folgt erst, wenn diese Kriterien erfüllt sind:

- mindestens zehn echte, dokumentierte Diagnoseeinsätze auf unterschiedlichen
  Kunden- oder Referenzsystemen;
- der Ablauf wurde von externen Technikern ohne direkte Einweisung erfolgreich
  durchgeführt;
- Diagnoseformat und Pseudonymisierungsregeln sind als kompatibler Vertrag
  festgelegt;
- keine offenen kritischen Defekte aus den realen Einsätzen;
- Release-, Prüf- und Supportweg sind dokumentiert und wiederholbar.

## Phase 0 – Öffentliche Nutzung starten

**Ziel:** v0.2.0 als nützliches, nachvollziehbares Community-Werkzeug
bereitstellen.

- Öffentliche GitHub-Pre-release mit Prüfsummen, Release Notes und
  Ausführungsanleitung.
- Anonymisiertes Beispiel-Diagnosepaket bereitstellen.
- Einen Sage-100-Fall und einen Fall ohne Sage vollständig durchspielen.
- Feedback über GitHub Issues und Discussions ermöglichen.
- Für jeden Einsatz festhalten, welche Informationen zusätzlich manuell
  beschafft werden mussten.

## Phase 1 – Aus echten Fällen lernen

**Start:** nach fünf bis zehn echten Paketen.

1. Analysebereitschaft als nachvollziehbare Checkliste: vorhandene Daten,
   fehlende Berechtigungen und die Auswirkung jeder Lücke.
2. Bewusste Exportprofile mit Vorschau: Basis, Performance und erweiterte
   technische Details.
3. Faktenbasierter Vergleich zweier Diagnosepakete, etwa vor/nach Update,
   Konfigurationsänderung oder Serverumzug.
4. Zusätzliche Collector und Checks ausschließlich dann, wenn sie bei echten
   Analysen wiederholt manuelle Arbeit ersparen.

## Phase 2 – Bezahltes Angebot ohne Pro-Software

**Ziel:** Den Wert einer professionellen Auswertung validieren, bevor ein
zweites Produkt entsteht.

Angebot: **ERPDetektiv Analyse**. Kundinnen und Kunden senden ein
Community-Diagnosepaket und erhalten einen priorisierten technischen Befund mit
Begründung, Risiko und Maßnahmenplan. Der Community-Download bleibt dabei
vollständig und nützlich.

## Phase 3 – Pro nur bei nachgewiesenem Muster

Eine Pro-Funktion wird erst gebaut, wenn dieselben manuellen Expertenschritte
in mehreren Fällen wiederkehren. Der wahrscheinlichste erste Baustein ist ein
zeitlich begrenztes Performance Capture mit Zeitreihen, Korrelation und einem
priorisierten Bericht – nicht ein Dashboard und nicht dauerhaftes Monitoring.

Spätere Systemhaus-Funktionen können Mehrkundenansicht, Vergleichshistorie,
Reportvorlagen und wiederholbare Capture-Vorlagen umfassen.

## Bewusste Leitplanken

- Community sammelt Fakten und zeigt objektive, nachvollziehbare Findings.
- Ein späteres Pro-Angebot soll Zeit für Einordnung und wiederholbare
  Expertenarbeit sparen, nicht Funktionen künstlich zurückhalten.
- SaaS, Dauer-Monitoring, Plugin-System und Mandantenverwaltung kommen nur bei
  nachgewiesenem Bedarf aus echten Einsätzen infrage.
