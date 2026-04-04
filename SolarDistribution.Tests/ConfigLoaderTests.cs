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

            // Heating ML 6.1 block
            Assert.IsNotNull(config.Heating, "Heating config must be present");
            Assert.AreEqual(300, config.Heating.SamplingIntervalSeconds, "Heating.SamplingIntervalSeconds mismatch");
            Assert.AreEqual("sensor.house_presence_mode", config.Heating.PresenceModeEntity, "Heating.PresenceModeEntity mismatch");
            Assert.AreEqual(180, config.Heating.MlTrainingWindowDays, "Heating.MlTrainingWindowDays mismatch");
            Assert.AreEqual(30, config.Heating.PurgeCompressionAgeDays, "Heating.PurgeCompressionAgeDays mismatch");

            Assert.IsTrue(config.Batteries.Count >= 2, "At least two batteries are expected in config.yaml for sample assertions");
            Assert.AreEqual(0.0, config.Batteries[0].SelfDischargePercentPerHour, "Battery[0].SelfDischargePercentPerHour mismatch");
            Assert.IsFalse(config.Batteries[0].PreventiveChargeOnlyIfEmptyBeforeSolar, "Battery[0].PreventiveChargeOnlyIfEmptyBeforeSolar mismatch");
        }
    }
}
