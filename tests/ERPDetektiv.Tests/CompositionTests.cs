using ERPDetektiv.Application;
using Xunit;

namespace ERPDetektiv.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void Standard_suite_contains_every_product_check_once()
    {
        var names = DiagnosticComposition.CreateChecks().Select(check => check.GetType().Name).ToList();

        Assert.Equal(
        [
            "DiskSpaceCheck", "QueryStoreCheck", "SqlMemoryCheck", "BackupAgeCheck",
            "QueryStoreCapacityCheck", "DatabaseAutogrowthCheck", "SqlMemoryRelativeToHostCheck",
            "PowerPlanCheck", "SageServiceStateCheck", "SageSoapServiceCheck",
            "SageAnonymousSDataEndpointCheck", "SageIsolationProcessStateCheck", "SqlIoLatencyCheck",
            "SqlPageLifeExpectancyCheck", "SqlMemoryGrantsPendingCheck", "SageMasterCountCheck",
            "SageDatasourceKeyConsistencyCheck", "SageBlobStorageRegistrationCheck",
            "SageDatasourceServerVersionCheck", "SageLargeAddressAwareClientCheck"
        ], names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}
