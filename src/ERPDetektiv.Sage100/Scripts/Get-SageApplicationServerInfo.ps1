# Gemeinsame Abfrage der Sage-Application-Server-Laufzeitdaten.
#
# Einzige Quelle fuer diese Abfrage. Sie wird von ERPDetektiv.Sage100 eingebettet.
#
# __SAGE_APPLICATION_SERVER_ROOT__ wird vom Aufrufer durch das
# Installationsverzeichnis ersetzt; einfache Anfuehrungszeichen sind dort bereits
# verdoppelt.
#
# Laeuft ausschliesslich in der 32-Bit-PowerShell: Die Sage-Administrations-
# bibliotheken sind 32-Bit und lassen sich aus einem 64-Bit-Prozess nicht laden.
#
# Bewusst NICHT erfasst: UserName, MandantName, DatabaseName und RequestContext.
# RequestContext enthaelt die vollstaendige SData-Anfrage inklusive Mandant,
# Datenbank und Hostname.

$ErrorActionPreference = 'Stop'
# Ohne diese Zeile serialisiert PowerShell den Fortschrittsstrom von
# Import-Module als CLIXML in den Fehlerstrom; das sieht in der Statusmeldung
# wie ein Fehler aus.
$ProgressPreference = 'SilentlyContinue'

$root = '__SAGE_APPLICATION_SERVER_ROOT__'
Set-Location -LiteralPath $root
Import-Module -Name (Join-Path $root 'Sagede.ApplicationServer.Administration.PowerShell.dll') -Force
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'Sagede.ApplicationServer.Administration.Client.dll'))

function Convert-DisplayMemoryToBytes([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return 0 }
    $normalized = $value.Trim() -replace '\s', ''
    if ($normalized -notmatch '^([0-9][0-9\.,]*)(B|KB|MB|GB|TB)$') { return 0 }
    $number = $matches[1]
    # Tausendertrenner entfernen, Dezimaltrenner vereinheitlichen: Die Anzeige
    # ist kulturabhaengig, der exportierte Wert darf es nicht sein.
    if ($number -match '[\.,]') {
        $last = [Math]::Max($number.LastIndexOf('.'), $number.LastIndexOf(','))
        $number = ($number.Substring(0, $last) -replace '[\.,]', '') + '.' + $number.Substring($last + 1)
    }
    $amount = [double]::Parse($number, [System.Globalization.CultureInfo]::InvariantCulture)
    $factor = switch ($matches[2]) { 'B' { 1 }; 'KB' { 1KB }; 'MB' { 1MB }; 'GB' { 1GB }; 'TB' { 1TB } }
    return [long]($amount * $factor)
}

function Get-DomainTimestamp($domain) {
    # Sage liefert den Zeitstempel als Zeichenkette im invarianten Format
    # (MM/dd/yyyy HH:mm:ss) - auch auf deutschsprachigen Servern. Deshalb zuerst
    # invariant parsen; die aktuelle Kultur laese "08/23/2026" als Tag 8,
    # Monat 23 und lieferte still $null. Gegen Sage 100 9.0 auf de-DE geprueft.
    $value = $domain.CreationDateTime
    if ($null -eq $value) { return $null }
    if ($value -is [datetime]) { return $value }
    $parsed = [datetime]::MinValue
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $styles = [System.Globalization.DateTimeStyles]::None
    if ([datetime]::TryParse([string]$value, $invariant, $styles, [ref]$parsed)) { return $parsed }
    return ([string]$value -as [datetime])
}

function Get-DomainId($domain) {
    # "Identifier" ist die stabile GUID der Service-Domain und damit die
    # Grundlage fuer Deltas ueber mehrere Messpunkte. "ID" existiert, ist aber oft
    # leer.
    foreach ($name in @('Identifier', 'ProcessId', 'InstanceID', 'ID')) {
        $property = $domain.PSObject.Properties[$name]
        if ($property -and $property.Value) { return [string]$property.Value }
    }
    return $null
}

# Feste Freigabeliste technischer Einstellungen. Alles, was nicht hier steht,
# verlaesst den Application Server nicht.
$allowed = @('enabled', 'isolationModel', 'poolInitialSize', 'poolMinSize', 'poolMaxSize', 'maxElasticCount',
    'maxUseServiceDomains', 'maxActiveServiceDomains', 'maxServiceDomainMemorySize', 'contextQuotaMax',
    'asyncQueuePollTime', 'serviceDomainPollTime', 'serviceDomainSynchronizationLockTimeout',
    'synchronizeContextSwitch', 'synchronizeContextSwitchTimeout', 'waitOnLockTime', 'maxRetries',
    'lifeTimeLeaseTime', 'lifetimeRenewOnCalltime', 'contextSwitchLevel', 'maxLogFileSize', 'newLogFile',
    'level', 'performanceCounter')

function Get-SectionPath($element) {
    # Der Elementname allein genuegt nicht: "serviceDomain" existiert sowohl unter
    # isolationServer als auch unter asyncIsolationServer, mit unterschiedlichen
    # Werten. Ohne Pfad steht poolMaxSize zweimal mit 15 und 30 im Export und
    # laesst sich keinem der beiden Isolationsserver mehr zuordnen.
    $parts = @()
    $current = $element
    while ($null -ne $current -and $current.NodeType -eq [System.Xml.XmlNodeType]::Element) {
        $parts = , $current.Name + $parts
        $current = $current.ParentNode
    }
    # Das Wurzelelement traegt keine Unterscheidung und faellt weg.
    if ($parts.Count -gt 1) { $parts = $parts[1..($parts.Count - 1)] }
    return ($parts -join '/')
}

$configuration = (Get-ServerConfiguration)[0]
[xml]$configurationXml = $configuration.Configuration
$settings = @($configurationXml.SelectNodes('//*') | ForEach-Object {
    $element = $_
    foreach ($attribute in $element.Attributes) {
        if ($allowed -contains $attribute.Name) {
            [pscustomobject]@{
                section = Get-SectionPath $element
                name    = $attribute.Name
                value   = $attribute.Value
            }
        }
    }
})

$domains = @((Get-ApplicationServerInfo).ServiceDomainInfos)
$now = Get-Date

# Je Domain ein Datensatz mit Identitaet: Die Summe ueber alle Domains ist nicht
# monoton, weil der Pool Domains erzeugt und verwirft. Aus der Summe allein
# lassen sich spaeter keine Deltas rechnen.
$domainRecords = @($domains | ForEach-Object {
    $domain = $_
    $created = Get-DomainTimestamp $domain
    [pscustomobject]@{
        id                   = Get-DomainId $domain
        serviceName          = [string]$domain.ServiceName
        state                = [string]$domain.State
        isActive             = [bool]$domain.IsActive
        forAsyncService      = [bool]$domain.ForAsyncService
        contextSwitches      = [long]$domain.ContextSwitches
        totalCalls           = [long]$domain.TotalCalls
        processorTimePercent = [int]$domain.PercentageProcessorTime
        windowsProcessId     = [int]$domain.WindowsProcessId
        workingSetBytes      = Convert-DisplayMemoryToBytes ([string]$domain.WorkingSet)
        createdAt            = if ($created) { $created.ToUniversalTime().ToString('o') } else { $null }
        ageSeconds           = if ($created) { [long][math]::Round(($now - $created).TotalSeconds) } else { $null }
    }
})

$oldest = @($domainRecords | Where-Object { $_.createdAt } | Sort-Object createdAt | Select-Object -First 1)
$isolation = [pscustomobject]@{
    serviceDomainCount      = $domains.Count
    activeCount             = @($domainRecords | Where-Object isActive).Count
    asyncCount              = @($domainRecords | Where-Object forAsyncService).Count
    appDomainIsolatedCount  = @($domains | Where-Object IsAppDomainIsolated).Count
    contextSwitchesTotal    = [long](@($domainRecords | Measure-Object -Property contextSwitches -Sum).Sum)
    totalCalls              = [long](@($domainRecords | Measure-Object -Property totalCalls -Sum).Sum)
    workingSetBytes         = [long](@($domainRecords | Measure-Object -Property workingSetBytes -Sum).Sum)
    oldestProcessStartedAt  = if ($oldest.Count -gt 0) { $oldest[0].createdAt } else { $null }
    oldestProcessAgeSeconds = if ($oldest.Count -gt 0) { $oldest[0].ageSeconds } else { $null }
    states                  = @($domainRecords | Group-Object state | ForEach-Object {
                                  [pscustomobject]@{ state = [string]$_.Name; count = $_.Count } })
}

$sdataProvider = New-Object 'Sagede.ApplicationServer.Administration.Client.WMI.SDataEndpointDataProvider'
$sdata = @($sdataProvider.FindEndpoints() | ForEach-Object {
    [pscustomobject]@{ binding = [string]$_.Binding; address = [string]$_.Address } })

$soapProvider = New-Object 'Sagede.ApplicationServer.Administration.Client.Model.Soap.SoapServiceInfoDataProvider'
$soap = @($soapProvider.FindSoapServiceDescriptions() | ForEach-Object {
    $service = $_
    # Operations kann Methode oder Eigenschaft sein; beide Formen abdecken.
    $operations = if ($service.PSObject.Methods['Operations']) { $service.Operations() } else { $service.Operations }
    [pscustomobject]@{
        name         = [string]$service.Name
        namespace    = [string]$service.Namespace
        moduleName   = [string]$service.ModuleName
        contractName = [string]$service.ContractName
        operations   = @($operations | ForEach-Object { [string]$_.Name })
    }
})

[pscustomobject]@{
    settings           = $settings
    isolationProcesses = $isolation
    sDataEndpoints     = $sdata
    soapServices       = $soap
    serviceDomains     = $domainRecords
} | ConvertTo-Json -Depth 8 -Compress
