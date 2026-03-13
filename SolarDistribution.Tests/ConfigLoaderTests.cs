using System.IO;
using NUnit.Framework;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Tests
{
    [TestFixture]
    public class ConfigLoaderTests
    {
        [Test]
        public void Load_ConfigYaml_Includes_NewOptions()
        {
            // Resolve repository config path from test working directory
            var baseDir = TestContext.CurrentContext.WorkDirectory;
            var configPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "config", "config.yaml"));

            Assert.IsTrue(File.Exists(configPath), $"config.yaml not found at {configPath}");

            var config = ConfigLoader.Load(configPath);

            Assert.IsNotNull(config);
            Assert.IsNotNull(config.Solar);

            // The config.yaml in repository sets these values — assert they are read correctly
            Assert.AreEqual(3, config.Polling.MaxConsecutiveAnomaliesBeforeAlert, "Polling.MaxConsecutiveAnomaliesBeforeAlert mismatch");
            Assert.AreEqual(5000, config.Solar.MaxPlausibleSurplusW, "Solar.MaxPlausibleSurplusW mismatch");
        }
    }
}
