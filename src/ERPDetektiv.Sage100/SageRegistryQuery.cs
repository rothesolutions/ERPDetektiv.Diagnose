using System.Diagnostics.CodeAnalysis;
using ERPDetektiv.Platform;

namespace ERPDetektiv.Sage100;

/// <summary>Fragt die Sage-Registry in einem einzigen PowerShell-Lauf ab.</summary>
/// <remarks>
/// Bewusst die 64-Bit-PowerShell: <c>WOW6432Node</c> ist dort ein regulaerer Pfad. Der
/// Umweg ueber die 32-Bit-Fassung – noetig fuer die Administrationsbibliotheken – waere
/// hier eine zusaetzliche Abhaengigkeit ohne Gegenwert.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class PowerShellSageRegistryCommandRunner(TimeSpan? timeout = null) : ISageRegistryCommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<SageRegistryCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellAsync(Script, _timeout, cancellationToken);
        return new SageRegistryCommandResult(result.ExitCode, result.StandardOutput, result.StandardError)
        {
            TimedOut = result.TimedOut
        };
    }

    /// <summary>Whitelist-Abfrage der Sage-Registry.</summary>
    /// <remarks>
    /// Aufgezaehlt statt angenommen wird alles, was zwischen Installationen variiert: der
    /// Versionsknoten, die SData-Bindings (ein System mit 9.0.11.x fuehrt zusaetzlich
    /// <c>HttpsSageId</c>), die Blobstorage-Endpunkte und die Applikationen unter
    /// <c>NamedUsers</c>. Auch die Ports sind keine Konstanten – gelesen werden die
    /// Adressen, nicht angenommene Portnummern.
    ///
    /// Nicht gelesen werden Lizenzschluessel, <c>LogiSoft</c>-Zugangsdaten, OTF-Schluessel,
    /// <c>#secVal#</c>-Werte, der <c>ApiKey</c> des Gateways sowie Mandanten- und
    /// Benutzernamen. Von Mandanten und benannten Benutzern geht nur die Anzahl in den
    /// Export.
    /// </remarks>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        $base = 'HKLM:\SOFTWARE\WOW6432Node\Sage'

        function Read-Value($path, $name) {
          if (-not (Test-Path -LiteralPath $path)) { return $null }
          $item = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
          if ($null -eq $item) { return $null }
          $property = $item.PSObject.Properties[$name]
          if ($null -eq $property) { return $null }
          return $property.Value
        }

        function Read-Children($path) {
          if (-not (Test-Path -LiteralPath $path)) { return @() }
          return @(Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue)
        }

        function Read-ValueNames($path) {
          if (-not (Test-Path -LiteralPath $path)) { return @() }
          $key = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
          if ($null -eq $key) { return @() }
          return @($key.GetValueNames() | Where-Object { $_ -ne '' })
        }

        $officeLine = Join-Path $base 'Office Line'
        $versions = @(Read-Children $officeLine | Where-Object { $_.PSChildName -as [version] })
        if ($versions.Count -eq 0) {
          [pscustomobject]@{ Found = $false } | ConvertTo-Json -Compress
          exit 0
        }
        $versionName = ($versions | Sort-Object { [version]$_.PSChildName } -Descending |
          Select-Object -First 1).PSChildName
        $root = Join-Path $officeLine $versionName

        $components = @(
          foreach ($product in Read-Children $base) {
            $productRoot = Read-Value (Join-Path $product.PSPath $versionName) 'Root'
            if ($null -ne $productRoot) {
              [pscustomobject]@{ Name = $product.PSChildName; InstallationPath = [string]$productRoot }
            }
          }
        )

        $applicationServers = @(
          foreach ($server in Read-Children (Join-Path $root 'Application Server')) {
            $endpoints = @(
              foreach ($endpoint in Read-Children (Join-Path $server.PSPath 'SData Endpoints')) {
                [pscustomobject]@{
                  Binding = $endpoint.PSChildName
                  Address = [string](Read-Value $endpoint.PSPath 'Address')
                  Authentication = [string](Read-Value $endpoint.PSPath 'Authentication')
                }
              }
            )
            [pscustomobject]@{
              Host = $server.PSChildName
              State = [string](Read-Value $server.PSPath 'State')
              Capability = [string](Read-Value $server.PSPath 'Capability')
              SDataEndpoints = $endpoints
            }
          }
        )

        $blobStorageServers = @(
          foreach ($server in Read-Children (Join-Path $root 'Blobstorage Server')) {
            $endpoints = @(
              foreach ($endpoint in Read-Children (Join-Path $server.PSPath 'Endpoints')) {
                [pscustomobject]@{
                  Binding = [string](Read-Value $endpoint.PSPath 'Name')
                  Address = [string](Read-Value $endpoint.PSPath 'Address')
                  Authentication = [string](Read-Value $endpoint.PSPath 'Authentication')
                }
              }
            )
            [pscustomobject]@{
              Host = $server.PSChildName
              Status = Read-Value $server.PSPath 'Status'
              Activated = Read-Value $server.PSPath 'Activated'
              Endpoints = $endpoints
            }
          }
        )

        $gateways = @(
          foreach ($gateway in Read-Children (Join-Path $root 'Application Gateway')) {
            [pscustomobject]@{
              Host = $gateway.PSChildName
              State = [string](Read-Value $gateway.PSPath 'State')
              Endpoint = [string](Read-Value $gateway.PSPath 'Endpoint')
            }
          }
        )

        $stations = @(
          foreach ($station in Read-Children (Join-Path $root 'Connectivity Server')) {
            [pscustomobject]@{
              Host = $station.PSChildName
              Endpoint = [string](Read-Value $station.PSPath 'Endpoint')
              State = Read-Value $station.PSPath 'State'
            }
          }
        )

        $datasources = @(
          foreach ($datasource in Read-Children (Join-Path $root 'Admin\Datasources')) {
            $mandators = @(Read-ValueNames (Join-Path $datasource.PSPath 'Mandators'))
            [pscustomobject]@{
              Name = $datasource.PSChildName
              ServerName = [string](Read-Value $datasource.PSPath 'ServerName')
              DatabaseName = [string](Read-Value $datasource.PSPath 'DatabaseName')
              Applications = [string](Read-Value $datasource.PSPath 'Applications')
              ServerVersion = Read-Value $datasource.PSPath 'ServerVersion'
              MandatorCount = $mandators.Count
            }
          }
        )

        $datasourceKeyNames = @(
          Read-ValueNames (Join-Path $root 'Security\DatasourceKeys') |
            Where-Object { $_ -like '#secVal#*' } |
            ForEach-Object { $_.Substring(9) }
        )

        $namedUsers = @(
          foreach ($application in Read-Children (Join-Path $root 'Security\NamedUsers')) {
            [pscustomobject]@{
              Application = $application.PSChildName
              Count = @(Read-ValueNames $application.PSPath).Count
            }
          }
        )

        $whitelist = @(
          @{ Path = 'Options'; Names = @('LargeAddressAwareClient', 'PreloadUi', 'APLockTimer',
              'MetaDataDataRevision', 'CentralAddInDirectory') },
          @{ Path = 'Security\Aufgaben-Center'; Names = @('VBScript') }
        )
        $values = @(
          foreach ($entry in $whitelist) {
            $entryPath = Join-Path $root $entry.Path
            foreach ($name in $entry.Names) {
              $value = Read-Value $entryPath $name
              if ($null -ne $value) {
                [pscustomobject]@{ Path = $entry.Path; Name = $name; Value = [string]$value }
              }
            }
          }
          $macroDebugger = Read-Value (Join-Path $officeLine 'MacroDebugger') 'IsEnabled'
          if ($null -ne $macroDebugger) {
            [pscustomobject]@{ Path = 'MacroDebugger'; Name = 'IsEnabled'; Value = [string]$macroDebugger }
          }
          if (Test-Path -LiteralPath (Join-Path $root 'LogiSoft')) {
            [pscustomobject]@{ Path = 'LogiSoft'; Name = 'Configured'; Value = 'true' }
          }
        )

        $sageServer = [string](Read-Value (Join-Path $root 'Server') 'Name')
        $isSageServer = $false
        $sageServerHost = $sageServer.TrimStart('\')
        if (-not [string]::IsNullOrWhiteSpace($sageServerHost)) {
          $isSageServer = ($sageServerHost -eq $env:COMPUTERNAME)
        }

        [pscustomobject]@{
          Found = $true
          ProductVersion = $versionName
          SageServer = $sageServer
          IsSageServer = $isSageServer
          Components = $components
          ApplicationServers = $applicationServers
          BlobStorageServers = $blobStorageServers
          Gateways = $gateways
          Stations = $stations
          Datasources = $datasources
          DatasourceKeyNames = $datasourceKeyNames
          NamedUsers = $namedUsers
          Values = $values
        } | ConvertTo-Json -Depth 6 -Compress
        """;
}
