# Release- und Runtime-Strategie

## Standarddownload

Der reguläre Download ist `ERPDetektiv-win-x64.zip`. Er enthält getrennte
Ordner `gui/` und `cli/` mit self-contained Windows-x64-Ausgaben einschließlich
der benötigten .NET-10-Laufzeit. Das Paket wird entpackt und ohne Installer
gestartet.

Diese Variante ist der Standard, weil auf Sage-100-Servern eine passende
.NET-Desktop-Runtime nicht vorausgesetzt werden kann. Der End-to-End-Test auf
der Sage-100-Referenz-VM hat diesen Fall bestätigt: Dort war keine
`dotnet`-Laufzeit installiert, die self-contained CLI lief dennoch erfolgreich.

## Optionales kleines Paket

Optional kann `ERPDetektiv-framework-dependent.zip` bereitgestellt werden. Es
ist für Entwicklungs- und Verwaltungsarbeitsplätze gedacht und benötigt eine
installierte .NET-10-Desktop-Runtime. Der Download und die Startdokumentation
müssen diese Voraussetzung klar benennen; er ist nicht der empfohlene
Serverdownload.

## Signierungsentscheidung

Die Community Edition wird zunächst **ohne öffentlich vertrauenswürdige
Code-Signatur** veröffentlicht. Der Grund ist wirtschaftlich: Die laufenden
Kosten und der wiederkehrende Verwaltungsaufwand eines eigenen Zertifikats
stehen bei der gegenwärtig kleinen, unregelmäßigen Auslieferung in keinem
Verhältnis zum Nutzen.

Stattdessen ist jeder Release nachvollziehbar: Er wird aus einem Git-Tag in CI
gebaut, enthält Lizenz- und Ausführungsdokumentation sowie eine
`SHA256SUMS.txt`. Administratorinnen und Administratoren prüfen damit die
Integrität des heruntergeladenen Pakets, bevor sie eine erforderliche lokale
Freigabe veranlassen.

Die Entscheidung wird neu bewertet, wenn mindestens einer dieser Fälle eintritt:

- regelmäßige externe Auslieferungen oder deutlicher Supportaufwand durch
  SmartScreen-/ASR-Warnungen;
- Aufnahme in ein kostenfreies Open-Source-Signierungsprogramm;
- kommerzielle Distribution, die eine eigene Herausgeberidentität im Zertifikat
  benötigt.

## Release-Härtung

- Versionsnummer und Release Notes pro Paket.
- SHA-256-Prüfsumme für jedes ZIP veröffentlichen.
- CI baut und testet beide Varianten; der self-contained `win-x64`-Build und
  SQL-Integrationstests gegen eine frische SQL-Server-Instanz sind verpflichtende
  Release-Gates.

## Prüfsummen

Zu jedem Paket veröffentlicht die CI eine `SHA256SUMS.txt`. Sie ist keine
Formalität: Ohne nachprüfbare Prüfsumme kann eine Administratorin die
Sicherheitsausnahme nicht verantworten, die das Werkzeug auf gehärteten
Systemen braucht.

## Ausführung auf gehärteten Systemen

Auf verwalteten Windows-Systemen wird ERPDetektiv regelmäßig blockiert, bevor
es startet – am häufigsten durch die ASR-Regel „Ausführung von ausführbaren
Dateien blockieren, wenn sie nicht die Kriterien Verbreitung, Alter oder
vertrauenswürdige Liste erfüllen".

Das ist ein **dauerhafter Bestandteil des Auslieferungswegs**, kein
Anfangsproblem: Die Regel bewertet die Verbreitung einer konkreten Datei, und
jede neue Version hat einen neuen Hash. Ein Werkzeug für einen kleinen
Fachkreis erreicht diese Schwelle nie.

Code-Signierung verbessert die Einstufung und macht die Ausnahme für den
Administrator begründbar, ersetzt sie aber nicht. Die Anleitung für den
Kundenkontakt steht in
[Wenn Windows die Ausführung blockiert](windows-ausfuehrung-blockiert.md) und
liegt jedem Paket bei.

## Spätere Code-Signierung

Seit Juni 2023 verlangen öffentlich vertrauenswürdige Zertifizierungsstellen,
dass der private Schlüssel auf zertifizierter Hardware liegt. Eine einfache
PFX-Datei in einem CI-Secret ist damit ausgeschlossen. Für eine spätere
Umstellung kommen in Frage:

- **Azure Trusted Signing** – günstig und für CI gebaut; setzt in der Regel
  eine mindestens dreijährig nachweisbare Organisation voraus.
- **Cloud-Signierdienst einer CA** (etwa DigiCert KeyLocker, SSL.com eSigner) –
  teurer, ebenfalls automatisierbar.
- **OV-Zertifikat mit USB-Token** – günstigste Variante, aber nur manuell
  signierbar.

Beim Signieren immer einen Zeitstempelserver angeben, sonst werden die
Signaturen mit Ablauf des Zertifikats ungültig. Der derzeit deaktivierte
Signierschritt in `.github/workflows/build.yml` bleibt als Integrationspunkt
erhalten.
