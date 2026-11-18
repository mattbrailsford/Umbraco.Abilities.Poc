using NUnit.Framework;

using Umbraco.Cms.Tests.Integration;

// Outside any namespace, so it runs once for every test in this project. Umbraco's own GlobalSetupTeardown loads the
// test settings (appsettings.Tests.json), but as an NUnit set-up fixture it only runs for its own assembly. The test
// server reads its database settings from it.
[SetUpFixture]
public sealed class CustomGlobalSetupTeardown
{
    private static GlobalSetupTeardown? _setupTearDown;

    [OneTimeSetUp]
    public void SetUp()
    {
        _setupTearDown = new GlobalSetupTeardown();
        _setupTearDown.SetUp();
    }

    [OneTimeTearDown]
    public void TearDown() => _setupTearDown?.TearDown();
}
