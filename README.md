[![Build status](https://ci.appveyor.com/api/projects/status/m9gpdbs7toa1348y?svg=true)](https://ci.appveyor.com/project/genemars/mig-homeauto)
[![NuGet](https://img.shields.io/nuget/v/MIG.HomeAutomation.svg)](https://www.nuget.org/packages/MIG.HomeAutomation/)
![License](https://img.shields.io/github/license/genielabs/mig-homeauto.svg)

# MIG.HomeAutomation

*[MIG](https://github.com/genielabs/mig-service-dotnet)* Interface for controlling home automation devices.

Implemented API domains:

- HomeAutomation.X10
- HomeAutomation.ZWave
- HomeAutomation.ZigBee

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/genielabs/mig-homeauto)


## Supported Hardware

- **X10**: CM11, CM15, CM19
- **Z-Wave**: most Z-Wave serial controllers
- **Zigbee**: For a list of supported controllers, see [ZigbeeNet](https://github.com/Mr-Markus/ZigbeeNet)

## ZigBee reporting bindings

Use these device commands with a 16-digit IEEE address, a device endpoint and a cluster (`0x0006` for On/Off or `0x0008` for Level Control).

```text
HomeAutomation.ZigBee/<ieee>/Binding.Get/<endpoint>/<cluster>
HomeAutomation.ZigBee/<ieee>/Binding.Ensure/<endpoint>/<cluster>
```

`Get` reads the device binding table. `Ensure` adds the unicast binding to the current coordinator only when missing and verifies it on the device. On success, `Bound` gives the binding state and `Outcome` is `Present`, `Absent`, `Added` or `AlreadyPresent`. A failed table read returns `Status: Error`.

`Reporting` describes the device's reporting configuration for attribute zero: `Configured`, `Disabled`, `NotConfigured`, `Unsupported` or `Unknown`. Successful readback includes `MinimumInterval` and `MaximumInterval` in seconds. Both commands preserve the device's reporting intervals and existing group bindings.

After explicit inclusion, HomeGenie attempts these bindings on the supported input clusters of standard ZigBee Home Automation and Light Link load endpoints. A node reannouncement does not initiate binding. An unsuccessful bind can be retried with `Binding.Ensure`.

## Related Projects

- https://github.com/genielabs/mig-service-dotnet
- https://github.com/genielabs/x10-lib-dotnet
- https://github.com/genielabs/cm19-lib-dotnet
- https://github.com/genielabs/zwave-lib-dotnet
- https://github.com/genielabs/homegenie

## License

This project is open source software, licensed under the terms of Apache license 2.0. See the [LICENSE](LICENSE) file for details.
