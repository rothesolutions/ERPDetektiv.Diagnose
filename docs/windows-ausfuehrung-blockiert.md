# Wenn Windows die Ausführung blockiert

Diese Anleitung richtet sich an Administratorinnen und Administratoren, die
ERPDetektiv auf einem verwalteten System ausführen sollen. Sie erklärt, wie sich
die Ursache in einer Minute bestimmen lässt und welche Freigabe jeweils nötig
ist.

Der häufigste Fall ist **kein Virenbefund**, sondern eine Härtungsregel, die
neue und wenig verbreitete Programme grundsätzlich blockiert.

## Kurzdiagnose

Auf dem betroffenen System in einer PowerShell mit erhöhten Rechten:

```powershell
$exe = 'C:\Program Files\ERPDetektiv\ERPDetektiv.Cli.exe'
"MOTW:      " + [bool](Get-Item $exe -Stream Zone.Identifier -ErrorAction SilentlyContinue)
"AppLocker: " + (Get-AppLockerPolicy -Effective -ErrorAction SilentlyContinue).RuleCollections.Count
"WDAC:      " + (Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue).CodeIntegrityPolicyEnforcementStatus
Get-WinEvent -LogName 'Microsoft-Windows-Windows Defender/Operational' -MaxEvents 40 -ErrorAction SilentlyContinue |
  Where-Object Id -in 1121,1122,1116,1117 | Format-Table TimeCreated, Id, Message -Wrap
```

Die Ereignisliste ist der entscheidende Teil. **1121** bedeutet: von einer
Attack-Surface-Reduction-Regel blockiert. **1116/1117** bedeutet: als
Schadsoftware erkannt. Kein Eintrag und `MOTW: True` deutet auf Mark of the Web.

## Fall 1: ASR-Regel „Verbreitung, Alter oder vertrauenswürdige Liste"

Symptom: **„Zugriff verweigert"** beim Start, unabhängig vom Verzeichnis und
auch als Administrator. Im Ereignis 1121 steht die Regel-ID

```text
01443614-cd74-433a-b99e-2ecdc07bfc25
```

Diese Regel blockiert ausführbare Dateien, die weder eine ausreichende
Verbreitung noch ein Mindestalter aufweisen und nicht auf einer
Vertrauensliste stehen. Sie ist Teil verbreiteter Sicherheitsbaselines.

**Sie bewertet die Datei, nicht den Pfad.** Das Programm in ein anderes
Verzeichnis zu kopieren hilft deshalb nicht.

ERPDetektiv trifft dieses Kriterium konstruktionsbedingt: Es ist ein Werkzeug
für einen kleinen Fachkreis, und **jede neue Version hat einen neuen Hash und
damit wieder keine Verbreitung**. Das ist kein Hinweis auf ein Problem mit dem
Programm, und es wird sich auch mit künftigen Versionen nicht von selbst
erledigen.

### Freigabe

Drei Wege, in aufsteigender Eingriffstiefe. Vorher die Prüfsumme abgleichen
(siehe unten).

Regelspezifischer Ausschluss – betrifft ausschließlich diese eine Regel:

```powershell
Add-MpPreference -AttackSurfaceReductionRules_RuleSpecificExclusions_Id '01443614-cd74-433a-b99e-2ecdc07bfc25' `
                 -AttackSurfaceReductionRules_RuleSpecificExclusions 'C:\Program Files\ERPDetektiv\*'
```

Ausschluss für alle ASR-Regeln, ohne den Virenschutz zu berühren:

```powershell
Add-MpPreference -AttackSurfaceReductionOnlyExclusions 'C:\Program Files\ERPDetektiv\'
```

Regel vorübergehend auf Überwachung setzen – reversibel und gut geeignet für
einen einmaligen Lauf:

```powershell
Add-MpPreference -AttackSurfaceReductionRules_Ids '01443614-cd74-433a-b99e-2ecdc07bfc25' `
                 -AttackSurfaceReductionRules_Actions AuditMode
```

Danach mit `-AttackSurfaceReductionRules_Actions Enabled` wieder scharf
schalten.

> In mit Intune oder Gruppenrichtlinien verwalteten Umgebungen setzt der
> nächste Richtlinienabgleich lokale Änderungen zurück. Die Ausnahme muss dann
> zentral eingetragen werden.

## Fall 2: Mark of the Web

Symptom: „Der Computer wurde durch Windows geschützt", mit der Möglichkeit
„Weitere Informationen → Trotzdem ausführen". Die Datei stammt aus einem
Download, aus einer Mail oder von einer Netzwerkfreigabe.

**Das Archiv vor dem Entpacken entsperren**, sonst erbt jede entpackte Datei
die Markierung:

```powershell
Unblock-File .\ERPDetektiv-win-x64.zip
Expand-Archive .\ERPDetektiv-win-x64.zip -DestinationPath 'C:\Program Files\ERPDetektiv'
```

Bereits entpackt:

```powershell
Get-ChildItem 'C:\Program Files\ERPDetektiv' -Recurse -File | Unblock-File
```

## Fall 3: AppLocker oder WDAC

Symptom: „Diese App wurde von Ihrem Systemadministrator blockiert." Die
Kurzdiagnose meldet dann Regelsammlungen beziehungsweise einen
Erzwingungsstatus ungleich 0.

Hier entscheidet die Unternehmensrichtlinie. Bei pfadbasierten Regeln genügt
meist ein zugelassenes Verzeichnis; bei herausgeberbasierten Regeln muss der
Herausgeber aufgenommen werden. Ereignisse stehen unter
`Microsoft-Windows-AppLocker/EXE und DLL`.

## Fall 4: Erkennung als Schadsoftware

Symptom: Ereignis 1116 oder 1117, Datei verschwindet oder ist nicht mehr
lesbar.

ERPDetektiv startet PowerShell, liest Prozessdaten und fragt Systemzeiten ab.
Das ist für eine Diagnose notwendig und entspricht zugleich einem Muster, auf
das Heuristiken ansprechen. Bitte in diesem Fall kurz Bescheid geben – wir
reichen die Datei als Falschmeldung beim Hersteller ein, das ist in der Regel
innerhalb weniger Tage erledigt.

## Prüfsumme abgleichen

Zu jedem Release gehört eine `SHA256SUMS.txt`. Vor jeder Freigabe:

```powershell
Get-FileHash .\ERPDetektiv-win-x64.zip -Algorithm SHA256 | Format-List
```

Der Wert muss mit dem veröffentlichten übereinstimmen. Eine Ausnahme sollte nur
für eine Datei eingetragen werden, deren Herkunft geprüft ist.

## Das richtige Paket

`ERPDetektiv-win-x64.zip` ist der Standarddownload. Er enthält die
.NET-Laufzeit und läuft ohne Vorinstallation.

`ERPDetektiv-framework-dependent.zip` ist deutlich kleiner, setzt aber eine
installierte .NET-10-Desktop-Runtime voraus. Auf Servern ist die meist nicht
vorhanden. Ob ein entpacktes Verzeichnis self-contained ist, zeigt:

```powershell
Test-Path 'C:\Program Files\ERPDetektiv\coreclr.dll'   # True = self-contained
```

Fehlt `coreclr.dll` und ist keine .NET-10-Runtime installiert, startet das
Programm auch nach jeder Freigabe nicht.

## Was das Werkzeug nicht tut

Für das Gespräch mit der Sicherheitsabteilung:

- Es liest ausschließlich. Sage 100, SQL Server und Betriebssystem werden nicht
  verändert.
- Es überträgt nichts. Kein Netzwerkversand, keine Telemetrie; der Export
  erfolgt durch bewusste Angabe eines Zielpfads.
- Es installiert nichts und richtet keinen Dienst ein.
- Es benötigt keine Administratorrechte. Erhöhte Rechte liefern lediglich
  zusätzliche, gekennzeichnete Informationen.
- Welche Daten erfasst werden, ist vollständig in
  [Erfasste Daten und Berechtigungen](collected-data-and-permissions.md)
  dokumentiert; der Quellcode steht unter Apache 2.0 offen.
