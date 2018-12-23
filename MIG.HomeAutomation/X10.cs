/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)
 
  Copyright (2012-2018) G-Labs (https://github.com/genielabs)

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

/*
 *     Author: Generoso Martello <gene@homegenie.it>
 *     Project Homepage: https://github.com/genielabs/mig-service-dotnet
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using MIG.Interfaces.HomeAutomation.Commons;
using MIG.Config;

using CM19Lib;
using CM19Lib.X10;

using XTenLib;

using RfCommandReceivedEventArgs = CM19Lib.Events.RfCommandReceivedEventArgs;

namespace MIG.Interfaces.HomeAutomation
{
    public class X10 : MigInterface
    {

        #region MigInterface API commands

        public enum Commands
        {
            NotSet,

            Parameter_Status,
            Control_On,
            Control_Off,
            Control_Bright,
            Control_Dim,
            Control_Level,
            Control_Level_Adjust,
            Control_Toggle,
            Control_AllLightsOn,
            Control_AllUnitsOff,
            Control_RfSend
        }

        #endregion

        #region Private fields

        private const string Cm19LibDriverPort = "CM19-USB";
        private const string Cm15LibDriverPort = "USB";
        private readonly Cm19Manager cm19Lib;
        private readonly XTenManager x10Lib;
        private Timer rfPulseTimer;
        private int RfPulseDelay = 300;
        private List<InterfaceModule> securityModules;
        private List<Option> options;
        // this is used to set the interface port (CM11 Serial, CM15 USB or CM19 USB)
        private string portName;
        private string lastAddressedModule;

        #endregion

        #region MIG Interface members

        public event InterfaceModulesChangedEventHandler InterfaceModulesChanged;
        public event InterfacePropertyChangedEventHandler InterfacePropertyChanged;

        public bool IsEnabled { get; set; }

        public List<Option> Options
        { 
            get
            {
                return options;
            }
            set
            {
                options = value;
                if (this.GetOption("Port") != null && this.GetOption("Port").Value != null)
                {
                    portName = this.GetOption("Port").Value.Replace("|", "/");
                }
                if (portName != Cm19LibDriverPort)
                {
                    // set x10Lib options
                    x10Lib.PortName = portName;
                    if (this.GetOption("HouseCodes") != null)
                        x10Lib.HouseCode = this.GetOption("HouseCodes").Value;
                }
            }
        }

        public void OnSetOption(Option option)
        {
            // parse option
            if (option.Name == "Port" && option.Value != null)
            {
                portName = this.GetOption("Port").Value.Replace("|", "/");
                Disconnect();
            }
            if (IsEnabled)
                Connect();
        }

        public List<InterfaceModule> GetModules()
        {
            InterfaceModule module = new InterfaceModule();
            List<InterfaceModule> modules = new List<InterfaceModule>();
            // CM15 / CM19 RF transceiver
            if (portName == Cm15LibDriverPort || portName == Cm19LibDriverPort)
            {
                module.Domain = this.GetDomain();
                module.Address = "RF";
                if (portName == Cm15LibDriverPort)
                {
                    module.Description = "CM15 Transceiver";
                }
                else
                {
                    module.Description = "CM19 Transceiver";
                }
                module.ModuleType = ModuleTypes.Sensor;
                modules.Add(module);
            }
            // Standard X10 modules
            foreach (var kv in x10Lib.Modules)
            {
                module = new InterfaceModule();
                module.Domain = this.GetDomain();
                module.Address = kv.Value.Code;
                module.ModuleType = ModuleTypes.Switch;
                module.Description = "X10 Module";
                modules.Add(module);
            }
            // CM15 / CM19 RF Security modules
            modules.AddRange(securityModules);
            return modules;
        }

        public bool Connect()
        {
            x10Lib.HouseCode = this.GetOption("HouseCodes").Value;
            if (portName == Cm19LibDriverPort)
            {
                // use CM19 driver
                OnInterfaceModulesChanged(this.GetDomain());
                return cm19Lib.Connect();
            }
            // use default driver for CM11 / CM15
            x10Lib.PortName = portName;
            OnInterfaceModulesChanged(this.GetDomain());
            return x10Lib.Connect();
        }

        public void Disconnect()
        {
            if (portName == Cm19LibDriverPort)
            {
                // use CM19 driver
                cm19Lib.Disconnect();
            }
            else
            {
                // use default driver for CM11 / CM15
                x10Lib.Disconnect();
            }
        }

        public bool IsConnected
        {
            get { return cm19Lib.IsConnected || x10Lib.IsConnected; }
        }

        public bool IsDevicePresent()
        {
            // AUTO-Detect CM15 / CM19 / CM21 devices
            //bool present = false;
            ////
            ////TODO: implement serial port scanning for CM11 as well
            //foreach (UsbRegistry usbdev in LibUsbDevice.AllDevices)
            //{
            //    //Console.WriteLine(o.Vid + " " + o.SymbolicName + " " + o.Pid + " " + o.Rev + " " + o.FullName + " " + o.Name + " ");
            //    if ((usbdev.Vid == 0x0BC7 && usbdev.Pid == 0x0001) || usbdev.FullName.ToUpper().Contains("X10"))
            //    {
            //        // CM15 - ActiveHome PLC interface
            //        cm15Found = true;
            //        break;
            //    }
            //    else if ((usbdev.Vid == 0x0BC7 && usbdev.Pid == 0x0002) || usbdev.FullName.ToUpper().Contains("X10"))
            //    {
            //        // CM19 - FireCracker Transceiver
            //        cm19Found = true;
            //        break;
            //    }
            //    else if ((usbdev.Vid == 0x0BC7 && usbdev.Pid == 0x0005) || usbdev.FullName.ToUpper().Contains("X10"))
            //    {
            //        // CM21 - NVidia ATI Remote Receiver
            //        cm21Found = true;
            //        break;
            //    }
            //}
            //return cm15Found || cm19Found;
            return true;
        }

        public object InterfaceControl(MigInterfaceCommand request)
        {
            ResponseText response = new ResponseText("OK");

            string nodeId = lastAddressedModule = request.Address;
            string option = request.GetOption(0);

            Commands command;
            Enum.TryParse<Commands>(request.Command.Replace(".", "_"), out command);

            if (portName == Cm19LibDriverPort)
            {
                // Parse house/unit
                var houseCode = CM19Lib.Utility.HouseCodeFromString(nodeId);
                var unitCode = CM19Lib.Utility.UnitCodeFromString(nodeId);
                var module = GetModuleByAddress(nodeId, ModuleTypes.Switch);
                // module.CustomData is used to store the current level
                switch (command)
                {
                case Commands.Control_On:
                    cm19Lib.UnitOn(houseCode, unitCode);
                    module.CustomData = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Off:
                    cm19Lib.UnitOff(houseCode, unitCode);
                    module.CustomData = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Bright:
                    cm19Lib.Bright(houseCode);
                    module.CustomData = module.CustomData + (5 / 100D);
                    if (module.CustomData > 1) module.CustomData = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Dim:
                    cm19Lib.Dim(houseCode);
                    module.CustomData = module.CustomData - (5 / 100D);
                    if (module.CustomData < 0) module.CustomData = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Level:
                    int dimValue = (int.Parse(option) - (int) (module.CustomData * 100.0)) / 5;
                    if (dimValue > 0)
                    {
                        for (int i = 0; i < dimValue; i++)
                        {
                            cm19Lib.Bright(houseCode);
                            Thread.Sleep(200);
                        }
                    }
                    else if (dimValue < 0)
                    {
                        for (int i = 0; i < -dimValue; i++)
                        {
                            cm19Lib.Dim(houseCode);
                            Thread.Sleep(200);
                        }
                    }
                    module.CustomData = module.CustomData + (dimValue * 5 / 100D);
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Toggle:
                    if (module.CustomData == 0D)
                    {
                        cm19Lib.UnitOn(houseCode, unitCode);
                        UpdateModuleLevel(module);
                    }
                    else
                    {
                        cm19Lib.UnitOff(houseCode, unitCode);
                        UpdateModuleLevel(module);
                    }
                    break;
                case Commands.Control_AllLightsOn:
                    cm19Lib.AllLightsOn(houseCode);
                    // TODO: update modules status
                    break;
                case Commands.Control_AllUnitsOff:
                    cm19Lib.AllUnitsOff(houseCode);
                    // TODO: update modules status
                    break;
                case Commands.Control_RfSend:
                    byte[] data = CM19Lib.Utility.StringToByteArray(option.Replace(" ", ""));
                    cm19Lib.SendMessage(data);
                    break;
                }
            }
            else
            {
                // Parse house/unit
                var houseCode = XTenLib.Utility.HouseCodeFromString(nodeId);
                var unitCode = XTenLib.Utility.UnitCodeFromString(nodeId);
                switch (command)
                {
                case Commands.Parameter_Status:
                    x10Lib.StatusRequest(houseCode, unitCode);
                    break;
                case Commands.Control_On:
                    x10Lib.UnitOn(houseCode, unitCode);
                    break;
                case Commands.Control_Off:
                    x10Lib.UnitOff(houseCode, unitCode);
                    break;
                case Commands.Control_Bright:
                    x10Lib.Bright(houseCode, unitCode, int.Parse(option));
                    break;
                case Commands.Control_Dim:
                    x10Lib.Dim(houseCode, unitCode, int.Parse(option));
                    break;
                case Commands.Control_Level_Adjust:
                    //int adjvalue = int.Parse(option);
                    //x10Lib.Modules[nodeId].Level = ((double)adjvalue/100D);
                    OnInterfacePropertyChanged(this.GetDomain(), nodeId, "X10 Module", ModuleEvents.Status_Level, x10Lib.Modules[nodeId].Level);
                    throw(new NotImplementedException("X10 CONTROL_LEVEL_ADJUST Not Implemented"));
                    break;
                case Commands.Control_Level:
                    int dimvalue = int.Parse(option) - (int)(x10Lib.Modules[nodeId].Level * 100.0);
                    if (dimvalue > 0)
                    {
                        x10Lib.Bright(houseCode, unitCode, dimvalue);
                    }
                    else if (dimvalue < 0)
                    {
                        x10Lib.Dim(houseCode, unitCode, -dimvalue);
                    }
                    break;
                case Commands.Control_Toggle:
                    string huc = XTenLib.Utility.HouseUnitCodeFromEnum(houseCode, unitCode);
                    if (x10Lib.Modules[huc].Level == 0)
                    {
                        x10Lib.UnitOn(houseCode, unitCode);
                    }
                    else
                    {
                        x10Lib.UnitOff(houseCode, unitCode);
                    }
                    break;
                case Commands.Control_AllLightsOn:
                    x10Lib.AllLightsOn(houseCode);
                    break;
                case Commands.Control_AllUnitsOff:
                    x10Lib.AllUnitsOff(houseCode);
                    break;
                case Commands.Control_RfSend:
                    byte[] data = CM19Lib.Utility.StringToByteArray("EB"+option.Replace(" ", ""));
                    x10Lib.SendMessage(data);
                    break;
                }
            }

            return response;
        }

        #endregion

        #region Lifecycle

        public X10()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                // Fix libusb symbolic link
                string assemblyFolder = MigService.GetAssemblyDirectory(this.GetType().Assembly);
                var libUsbLink = Path.Combine(assemblyFolder, "libusb-1.0.so");
                if (File.Exists(libUsbLink)) File.Delete(libUsbLink);
                // RaspBerry Pi arm-hf (hard-float) dependency check and needed symlink
                if (File.Exists("/lib/arm-linux-gnueabihf/libusb-1.0.so.0.1.0"))
                {
                    MigService.ShellCommand("ln", " -s \"/lib/arm-linux-gnueabihf/libusb-1.0.so.0.1.0\" \"" + libUsbLink + "\"");
                }
                // RaspBerry Pi arm-el dependency check and needed symlink
                else if (File.Exists("/lib/arm-linux-gnueabi/libusb-1.0.so.0.1.0"))
                {
                    MigService.ShellCommand("ln", " -s \"/lib/arm-linux-gnueabi/libusb-1.0.so.0.1.0\" \"" + libUsbLink + "\"");
                }
                // Debian/Ubuntu 64bit dependency and needed symlink check
                else if (File.Exists("/lib/x86_64-linux-gnu/libusb-1.0.so.0"))
                {
                    MigService.ShellCommand("ln", " -s \"/lib/x86_64-linux-gnu/libusb-1.0.so.0\" \"" + libUsbLink + "\"");
                }
                // Remove CM19 kernel drivers to allow access to the device
                MigService.ShellCommand("rmmod", " lirc_atiusb");
                MigService.ShellCommand("rmmod", " ati_remote");
                MigService.ShellCommand("rmmod", " rc_ati_x10");
            }

            // CM19 Transceiver driver
            // TODO: should "rmmod" CM19 kernel modules for CM19Lib to work (Linux)
            cm19Lib = new Cm19Manager();
            cm19Lib.RfCameraReceived += Cm19LibOnRfCameraReceived;
            cm19Lib.RfDataReceived += Cm19LibOnRfDataReceived;
            cm19Lib.RfCommandReceived += Cm19LibOnRfCommandReceived;
            cm19Lib.RfSecurityReceived += Cm19LibOnRfSecurityReceived;

            // CM11 and CM15 PLC driver
            x10Lib = new XTenManager();
            x10Lib.ModuleChanged += X10lib_ModuleChanged;
            x10Lib.RfDataReceived += X10lib_RfDataReceived;
            x10Lib.RfSecurityReceived += X10lib_RfSecurityReceived;

            securityModules = new List<InterfaceModule>();
        }

        #endregion

        #region Private fields

        #region Utility methods
        
        private InterfaceModule AddModule(ModuleTypes moduleType, string address)
        {
            InterfaceModule module = new InterfaceModule();
            module.Domain = this.GetDomain();
            module.Address = address;
            module.Description = "X10 Security";
            module.ModuleType = moduleType;
            module.CustomData = 0D;
            securityModules.Add(module);
            OnInterfacePropertyChanged(module.Domain, "RF", "X10 RF Receiver", ModuleEvents.Receiver_Status, "Added module " + address + " (" + moduleType + ")");
            OnInterfaceModulesChanged(module.Domain);
            return module;
        }

        private void ParseModuleAddress(String eventAddress, String eventName, out ModuleTypes moduleType, out string address)
        {
            address = "S-" + eventAddress;
            moduleType = ModuleTypes.Sensor;
            if (eventName.StartsWith("DoorSensor1_"))
            {
                address += "01";
                moduleType = ModuleTypes.DoorWindow;
            }
            else if (eventName.StartsWith("DoorSensor2_"))
            {
                address += "02";
                moduleType = ModuleTypes.DoorWindow;
            }
            else if (eventName.StartsWith("Motion_"))
            {
                moduleType = ModuleTypes.Sensor;
            }
            else if (eventName.StartsWith("Remote_"))
            {
                address = "S-REMOTE";
                moduleType = ModuleTypes.Sensor;
            }
        }

        private InterfaceModule GetModuleByAddress(string address, ModuleTypes? defaultType = null)
        {
            var module = securityModules.Find(m => m.Address == address);
            if (module == null && defaultType != null)
            {
                module = AddModule((ModuleTypes)defaultType, address);
            }
            return module;
        }

        private void UpdateModuleLevel(InterfaceModule module)
        {
            OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Module", ModuleEvents.Status_Level, module.CustomData.ToString(CultureInfo.InvariantCulture));
        }
        
        #endregion

        #region CM19Lib events
        
        private void Cm19LibOnRfSecurityReceived(object sender, CM19Lib.Events.RfSecurityReceivedEventArgs args)
        {
            ModuleTypes moduleType; string address;
            ParseModuleAddress(args.Address.ToString("X6"), args.Event.ToString(), out moduleType, out address);
            var module = GetModuleByAddress(address, moduleType);
            switch (args.Event)
            {
            case RfSecurityEvent.DoorSensor1_Alert:
            case RfSecurityEvent.DoorSensor2_Alert:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 0);
                break;
            case RfSecurityEvent.DoorSensor1_Alert_Tarmper:
            case RfSecurityEvent.DoorSensor2_Alert_Tamper:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 1);
                break;
            case RfSecurityEvent.DoorSensor1_Normal:
            case RfSecurityEvent.DoorSensor2_Normal:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 0);
                break;
            case RfSecurityEvent.DoorSensor1_Normal_Tamper:
            case RfSecurityEvent.DoorSensor2_Normal_Tamper:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 1);
                break;
            case RfSecurityEvent.DoorSensor1_BatteryLow:
            case RfSecurityEvent.DoorSensor2_BatteryLow:
            case RfSecurityEvent.Motion_BatteryLow:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Battery, 10);
                break;
            case RfSecurityEvent.DoorSensor1_BatteryOk:
            case RfSecurityEvent.DoorSensor2_BatteryOk:
            case RfSecurityEvent.Motion_BatteryOk:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Battery, 100);
                break;
            case RfSecurityEvent.Motion_Alert:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                break;
            case RfSecurityEvent.Motion_Normal:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                break;
            case RfSecurityEvent.Remote_ArmAway:
            case RfSecurityEvent.Remote_ArmHome:
            case RfSecurityEvent.Remote_Disarm:
            case RfSecurityEvent.Remote_Panic:
            case RfSecurityEvent.Remote_Panic_15:
            case RfSecurityEvent.Remote_LightOn:
            case RfSecurityEvent.Remote_LightOff:
                var evt = args.Event.ToString();
                evt = evt.Substring(evt.IndexOf('_') + 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Remote", ModuleEvents.Sensor_Key, evt);
                break;
            }
        }

        private void Cm19LibOnRfDataReceived(object sender, CM19Lib.Events.RfDataReceivedEventArgs args)
        {
            var code = BitConverter.ToString(args.Data).Replace("-", " ");
            OnInterfacePropertyChanged(this.GetDomain(), "RF", "X10 RF Receiver", ModuleEvents.Receiver_RawData, code);
            if (rfPulseTimer == null)
            {
                rfPulseTimer = new Timer(delegate(object target)
                {
                    OnInterfacePropertyChanged(this.GetDomain(), "RF", "X10 RF Receiver", ModuleEvents.Receiver_RawData, "");
                });
            }
            rfPulseTimer.Change(RfPulseDelay, Timeout.Infinite);
        }

        // TODO: Cm19LibOnRfCameraReceived not implemented (perhaps not needed)
        private void Cm19LibOnRfCameraReceived(object sender, RfCommandReceivedEventArgs args)
        {
            /*
            var address = lastAddressedModule = "CAMERA-" + args.HouseCode;
            switch (args.Command)
            {
                    case RfFunction.CameraLeft:
                        break;
                    case RfFunction.CameraUp:
                        break;
                    case RfFunction.CameraRight:
                        break;
                    case RfFunction.CameraDown:
                        break;
            }
            */
            // TODO: not implemented (perhaps not needed)
        }

        private void Cm19LibOnRfCommandReceived(object sender, RfCommandReceivedEventArgs args)
        {
            var address = lastAddressedModule;
            if (args.UnitCode != UnitCode.UnitNotSet)
            {
                address = lastAddressedModule = args.HouseCode + args.UnitCode.ToString().Replace("Unit_", "");
            }
            var module = GetModuleByAddress(address, ModuleTypes.Sensor);
            switch (args.Command)
            {
                case Function.On:
                    module.CustomData = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Off:
                    module.CustomData = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Bright:
                    module.CustomData = module.CustomData + 1D/22D;
                    if (module.CustomData > 1) module.CustomData = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Dim:
                    module.CustomData = module.CustomData - 1D/22D;
                    if (module.CustomData < 0) module.CustomData = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Function.AllLightsOn:
                    // TODO: not implemented
                    break;
                case Function.AllUnitsOff:
                    // TODO: not implemented
                    break;
            }
        }

        #endregion
        
        #region XTenLib events

        private void X10lib_RfSecurityReceived(object sender, RfSecurityReceivedEventArgs args)
        {
            ModuleTypes moduleType; string address;
            ParseModuleAddress(args.Address.ToString("X6"), args.Event.ToString(), out moduleType, out address);
            var module = GetModuleByAddress(address, moduleType);
            switch (args.Event)
            {
            case X10RfSecurityEvent.DoorSensor1_Alert:
            case X10RfSecurityEvent.DoorSensor2_Alert:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 0);
                break;
            case X10RfSecurityEvent.DoorSensor1_Alert_Tarmper:
            case X10RfSecurityEvent.DoorSensor2_Alert_Tamper:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 1);
                break;
            case X10RfSecurityEvent.DoorSensor1_Normal:
            case X10RfSecurityEvent.DoorSensor2_Normal:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 0);
                break;
            case X10RfSecurityEvent.DoorSensor1_Normal_Tamper:
            case X10RfSecurityEvent.DoorSensor2_Normal_Tamper:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Sensor_Tamper, 1);
                break;
            case X10RfSecurityEvent.DoorSensor1_BatteryLow:
            case X10RfSecurityEvent.DoorSensor2_BatteryLow:
            case X10RfSecurityEvent.Motion_BatteryLow:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Battery, 10);
                break;
            case X10RfSecurityEvent.DoorSensor1_BatteryOk:
            case X10RfSecurityEvent.DoorSensor2_BatteryOk:
            case X10RfSecurityEvent.Motion_BatteryOk:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Battery, 100);
                break;
            case X10RfSecurityEvent.Motion_Alert:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 1);
                break;
            case X10RfSecurityEvent.Motion_Normal:
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Sensor", ModuleEvents.Status_Level, 0);
                break;
            case X10RfSecurityEvent.Remote_ArmAway:
            case X10RfSecurityEvent.Remote_ArmHome:
            case X10RfSecurityEvent.Remote_Disarm:
            case X10RfSecurityEvent.Remote_Panic:
            case X10RfSecurityEvent.Remote_Panic_15:
            case X10RfSecurityEvent.Remote_LightOn:
            case X10RfSecurityEvent.Remote_LightOff:
                var evt = args.Event.ToString();
                evt = evt.Substring(evt.IndexOf('_') + 1);
                OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Security Remote", ModuleEvents.Sensor_Key, evt);
                break;
            }
        }

        private void X10lib_RfDataReceived(object sender, RfDataReceivedEventArgs args)
        {
            var code = BitConverter.ToString(args.Data).Replace("-", " ");
            OnInterfacePropertyChanged(this.GetDomain(), "RF", "X10 RF Receiver", ModuleEvents.Receiver_RawData, code);
            if (rfPulseTimer == null)
            {
                rfPulseTimer = new Timer(delegate(object target)
                {
                    OnInterfacePropertyChanged(this.GetDomain(), "RF", "X10 RF Receiver", ModuleEvents.Receiver_RawData, "");
                });
            }
            rfPulseTimer.Change(RfPulseDelay, Timeout.Infinite);
        }

        private void X10lib_ModuleChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Level")
                OnInterfacePropertyChanged(this.GetDomain(), (sender as X10Module).Code, (sender as X10Module).Description, ModuleEvents.Status_Level, (sender as X10Module).Level.ToString(CultureInfo.InvariantCulture));
        }

        #endregion

        #region Events

        protected virtual void OnInterfaceModulesChanged(string domain)
        {
            if (InterfaceModulesChanged != null)
            {
                var args = new InterfaceModulesChangedEventArgs(domain);
                InterfaceModulesChanged(this, args);
            }
        }

        protected virtual void OnInterfacePropertyChanged(string domain, string source, string description, string propertyPath, object propertyValue)
        {
            if (InterfacePropertyChanged != null)
            {
                var args = new InterfacePropertyChangedEventArgs(domain, source, description, propertyPath, propertyValue);
                InterfacePropertyChanged(this, args);
            }
        }

        #endregion

        #endregion

    }
}
