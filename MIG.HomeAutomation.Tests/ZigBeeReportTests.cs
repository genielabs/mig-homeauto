using System.Collections.Generic;
using MIG.Config;
using MIG.Interfaces.HomeAutomation;
using NUnit.Framework;
using ZigBeeNet.Serialization;
using ZigBeeNet.ZCL.Field;
using ZigBeeNet.ZCL.Clusters;
using ZigBeeNet.ZCL.Clusters.General;
using ZigBeeNet.ZCL.Clusters.IasZone;

namespace MIG.HomeAutomation.Tests;

public class ZigBeeReportTests
{
    private const string Address = "0017880100B1A9A7";

    private sealed class Driver : ZigBee
    {
        public readonly List<(string Path, object Value)> Events = new();

        protected override void OnInterfacePropertyChanged(string domain, string source, string description, string path, object value)
        {
            Assert.That(source, Is.EqualTo(Address));
            Events.Add((path, value));
        }

        public void Report(ushort cluster, params byte[] payload)
        {
            var report = new ReportAttributesCommand { ClusterId = cluster, Reports = new List<AttributeReport>() };
            var reader = new DefaultDeserializer(payload);
            while (!reader.IsEndOfStream())
            {
                var attribute = new AttributeReport();
                attribute.Deserialize(reader);
                report.Reports.Add(attribute);
            }
            ReceiveReport(Address, report);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OnlyMeasuredTemperatureIsPublishedRegardlessOfAttributeOrder(bool measuredFirst)
    {
        var driver = new Driver();
        byte[] minimum = { 1, 0, 0x29, 0x60, 0xF0 };
        byte[] measured = { 0, 0, 0x29, 0x06, 0xFF }; // -2.50 °C
        var payload = new List<byte>(measuredFirst ? measured : minimum);
        payload.AddRange(measuredFirst ? minimum : measured);
        driver.Report(ZclTemperatureMeasurementCluster.CLUSTER_ID, payload.ToArray());
        Assert.That(driver.Events, Is.EqualTo(new (string, object)[] { ("Sensor.Temperature", -2.5) }));
    }

    [Test]
    public void AllValidHumidityReadingsAreProcessedAndInvalidMeasurementsIgnored()
    {
        var driver = new Driver();
        driver.Report(ZclRelativeHumidityMeasurementCluster.CLUSTER_ID,
            0, 0, 0x21, 0xE2, 0x1E, 0, 0, 0x21, 0xFF, 0xFF, 0, 0, 0x21, 0, 0);
        driver.Report(ZclTemperatureMeasurementCluster.CLUSTER_ID, 0, 0, 0x29, 0, 0x80);
        driver.Report(ZclLevelControlCluster.CLUSTER_ID, 0, 0, 0x20, 0xFF);
        driver.Report(ZclIlluminanceMeasurementCluster.CLUSTER_ID, 0, 0, 0x21, 0xFF, 0xFF);
        Assert.That(driver.Events, Is.EqualTo(new (string, object)[] { ("Sensor.Humidity", 79.06), ("Sensor.Humidity", 0D) }));
    }

    [Test]
    public void IlluminanceIsConvertedToLuxAndOccupancyUsesOnlyItsDefinedBit()
    {
        var driver = new Driver();
        driver.Report(ZclIlluminanceMeasurementCluster.CLUSTER_ID, 0, 0, 0x21, 0x21, 0x4E, 0, 0, 0x21, 0, 0);
        driver.Report(ZclOccupancySensingCluster.CLUSTER_ID, 0, 0, 0x18, 3, 0, 0, 0x18, 2);
        driver.Report(ZclIlluminanceLevelSensingCluster.CLUSTER_ID, 0, 0, 0x30, 2);
        Assert.That(driver.Events, Is.EqualTo(new (string, object)[] {
            ("Sensor.Luminance", 100D), ("Sensor.Luminance", 0D), ("Sensor.MotionDetect", 1),
            ("Sensor.MotionDetect", 0), ("ZigBeeNode.IlluminanceLevelStatus", (byte)2)
        }));
    }

    [TestCase(8, 0, 0, 1)]
    [TestCase(4, 0, 1, 0)]
    [TestCase(2, 1, 0, 0)]
    [TestCase(13, 1, 1, 1)]
    [TestCase(0, 0, 0, 0)]
    public void ZoneNotificationsAndAttributeReportsSeparateAlarmTamperAndLowBattery(int bits, int alarm, int tamper, int lowBattery)
    {
        var driver = new Driver();
        var expected = new (string, object)[] { ("ZigBeeNode.ZoneStatus", bits), ("Status.Level", alarm),
            ("Sensor.Tamper", tamper), ("ZigBeeNode.BatteryLow", lowBattery) };
        driver.ReceiveReport(Address, new ZoneStatusChangeNotificationCommand { ZoneStatus = (ushort)bits });
        Assert.That(driver.Events, Is.EqualTo(expected));
        driver.Events.Clear();
        driver.Report(ZclIasZoneCluster.CLUSTER_ID, 2, 0, 0x19, (byte)bits, 0);
        Assert.That(driver.Events, Is.EqualTo(expected));
    }

    [Test]
    public void RemoteChangesUpdateTheLevelUsedForToggleAndPreserveTheLastBrightness()
    {
        var driver = new Driver();
        var data = new ZigBeeNodeData();
        driver.GetModules().Add(new InterfaceModule { Address = Address, CustomData = data });
        driver.Report(ZclOnOffCluster.CLUSTER_ID, 0, 0, 0x10, 1);
        driver.Report(ZclLevelControlCluster.CLUSTER_ID, 0, 0, 0x20, 127);
        driver.Report(ZclOnOffCluster.CLUSTER_ID, 0, 0, 0x10, 0);
        Assert.That(data.Level, Is.Zero);
        data.SetOnOff(data.Level <= 0);
        Assert.That(data.Level, Is.EqualTo(0.5));
        driver.Report(ZclOnOffCluster.CLUSTER_ID, 0, 0, 0x10, 1);
        Assert.That(driver.Events, Is.EqualTo(new (string, object)[] { ("Status.Level", 1D), ("Status.Level", 0.5), ("Status.Level", 0D), ("Status.Level", 0.5) }));
    }
}
