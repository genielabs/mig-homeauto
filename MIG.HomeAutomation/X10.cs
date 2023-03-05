/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)
 
  Copyright (2012-2023) G-Labs (https://github.com/genielabs)

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

/*
 *
 * Unified driver interface for X10: CM11 (serial), CM15 (USB), CM19 (USB)
 *
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Xml.Serialization;

using MIG.Config;
using MIG.Interfaces.HomeAutomation.Commons;

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
        private const string SecurityModulesDb = "x10_security_modules.xml";
        private readonly Cm19Manager cm19Lib;
        private readonly XTenManager x10Lib;
        private List<InterfaceModule> securityModules;
        // the standardModules list is used by CM19Lib since it has not its own modules list  like XTenLib
        List<InterfaceModule> standardModules;
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
                var portOption = this.GetOption("Port");
                if (portOption != null && portOption.Value != null)
                {
                    OnSetOption(portOption);
                }
            }
        }

        public void OnSetOption(Option option)
        {
            // parse option
            if (option.Name == "Port" && option.Value != null)
            {
                Disconnect();
                portName = this.GetOption("Port").Value.Replace("|", "/");
            }
            else if (option.Name == "HouseCodes" && option.Value != null)
            {
                // this option ends up in XTenLib to build the modules list that is used by CM19Lib as well
                var houseCodes = this.GetOption("HouseCodes");
                x10Lib.HouseCode = houseCodes.Value; // this will rebuild XTenLib.Modules list
                // Build CM19Lib module list to store modules Level value (uses XTenLib.Modules to enumerate standard modules)
                standardModules.Clear();
                foreach (var m in x10Lib.Modules)
                {
                    var module = new InterfaceModule();
                    module.Domain = this.GetDomain();
                    module.Address = m.Value.Code;
                    module.Description = m.Value.Description;
                    module.CustomData = new X10ModuleData()
                    {
                        Level = m.Value.Level,
                        Type = ModuleTypes.Generic
                    };
                    standardModules.Add(module);
                }
                OnInterfaceModulesChanged(this.GetDomain());
            }
            // re-connect if an interface option is updated
            if (IsEnabled) Connect();
        }

        public List<InterfaceModule> GetModules()
        {
            var module = new InterfaceModule();
            var modules = new List<InterfaceModule>();
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
                module.CustomData = new X10ModuleData()
                {
                    Type = ModuleTypes.Sensor            
                };
                modules.Add(module);
            }
            // Standard X10 modules
            foreach (var kv in x10Lib.Modules)
            {
                module = new InterfaceModule();
                module.Domain = this.GetDomain();
                module.Address = kv.Value.Code;
                module.CustomData = new X10ModuleData()
                {
                    Type = ModuleTypes.Generic            
                };
                module.Description = "X10 Module";
                modules.Add(module);
            }
            // CM15 / CM19 RF Security modules
            modules.AddRange(securityModules);
            return modules;
        }

        public bool Connect()
        {
            if (portName == Cm19LibDriverPort)
            {
                // use CM19 driver
                return cm19Lib.Connect();
            }
            // else use default driver for CM11 / CM15
            x10Lib.PortName = portName;
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
            var response = new ResponseText("OK");

            string nodeId = lastAddressedModule = request.Address;
            string option = request.GetOption(0);

            Commands command;
            Enum.TryParse<Commands>(request.Command.Replace(".", "_"), out command);

            if (portName == Cm19LibDriverPort)
            {
                // Parse house/unit
                var houseCode = CM19Lib.Utility.HouseCodeFromString(nodeId);
                var unitCode = CM19Lib.Utility.UnitCodeFromString(nodeId);
                var module = GetSecurityModuleByAddress(nodeId, ModuleTypes.Generic);
                // module.CustomData is used to store the current level
                switch (command)
                {
                case Commands.Control_On:
                    cm19Lib.UnitOn(houseCode, unitCode);
                    module.CustomData.Level = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Off:
                    cm19Lib.UnitOff(houseCode, unitCode);
                    module.CustomData.Level = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Bright:
                    cm19Lib.Bright(houseCode);
                    module.CustomData.Level += (5 / 100D);
                    if (module.CustomData.Level > 1) module.CustomData.Level = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Dim:
                    cm19Lib.Dim(houseCode);
                    module.CustomData.Level -= (5 / 100D);
                    if (module.CustomData.Level < 0) module.CustomData.Level = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Level:
                    int dimValue = (int.Parse(option) - (int) (module.CustomData.Level * 100.0)) / 5;
                    if (dimValue > 0)
                    {
                        cm19Lib.Bright(houseCode);
                        for (int i = 0; i < (dimValue / Cm19Manager.SendRepeatCount); i++)
                        {
                            cm19Lib.Bright(houseCode);
                        }
                    }
                    else if (dimValue < 0)
                    {
                        cm19Lib.Dim(houseCode);
                        for (int i = 0; i < -(dimValue / Cm19Manager.SendRepeatCount); i++)
                        {
                            cm19Lib.Dim(houseCode);
                        }
                    }
                    module.CustomData.Level += (dimValue * 5 / 100D);
                    UpdateModuleLevel(module);
                    break;
                case Commands.Control_Toggle:
                    if (module.CustomData.Level == 0D)
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
                    // SendRepeatCount is not implemented in XTenLib, so a for loop in required here
                    for (int i = 0; i < Cm19Manager.SendRepeatCount; i++)
                    {
                        x10Lib.SendMessage(data);
                        Thread.Sleep(Cm19Manager.SendPauseMs);
                    }
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
            // try loading cached security modules list
            DeserializeModules(SecurityModulesDb, securityModules);
            standardModules = new List<InterfaceModule>();
        }

        #endregion

        #region Private fields

        #region Utility methods
        
        private void SerializeModules(string fileName, List<InterfaceModule> list) {
            try
            {
                XmlAttributeOverrides overrides = new XmlAttributeOverrides();
                XmlAttributes attribs = new XmlAttributes();
                attribs.XmlIgnore = true;
                attribs.XmlElements.Add(new XmlElementAttribute("CustomData"));
                overrides.Add(typeof(InterfaceModule), "CustomData", attribs);
                var serializer = new XmlSerializer(typeof(List<InterfaceModule>), overrides);
                using ( var stream = File.OpenWrite(GetDbFullPath(fileName)))
                {
                    serializer.Serialize(stream, list);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
        private void DeserializeModules(string fileName, List<InterfaceModule> list) {
            try
            {
                var serializer = new XmlSerializer(typeof(List<InterfaceModule>));
                using ( var stream = File.OpenRead(GetDbFullPath(fileName)) )
                {
                    var other = (List<InterfaceModule>)(serializer.Deserialize(stream));
                    list.Clear();
                    list.AddRange(other);
                }
            }
            catch (Exception e)
            {
                //Console.WriteLine(e);
            }
        }

        private static string GetDbFullPath(string file)
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mig", "x10");
            Directory.CreateDirectory(path);
            return Path.Combine(path, file);
        }

        private InterfaceModule AddSecurityModule(ModuleTypes moduleType, string address)
        {
            InterfaceModule module = new InterfaceModule();
            module.Domain = this.GetDomain();
            module.Address = address;
            module.Description = "X10 Security";
            module.CustomData = new X10ModuleData()
            {
                Level = 0D,
                Type = moduleType            
            };
            securityModules.Add(module);
            SerializeModules(SecurityModulesDb, securityModules);
            OnInterfacePropertyChanged(module.Domain, "RF", "X10 RF Receiver", ModuleEvents.Receiver_Status, "Added module " + address + " (" + moduleType + ")");
            OnInterfaceModulesChanged(module.Domain);
            return module;
        }

        private void ParseSecurityModuleAddress(String eventAddress, String eventName, out ModuleTypes moduleType, out string address)
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

        private InterfaceModule GetSecurityModuleByAddress(string address, ModuleTypes? defaultType = null)
        {
            var module = securityModules.Find(m => m.Address == address);

            if (module == null && defaultType != null)
            {
                module = AddSecurityModule((ModuleTypes)defaultType, address);
            }
            return module;
        }

        private void UpdateModuleLevel(InterfaceModule module)
        {
            OnInterfacePropertyChanged(module.Domain, module.Address, "X10 Module", ModuleEvents.Status_Level, module.CustomData.Level.ToString(CultureInfo.InvariantCulture));
        }
        
        #endregion

        #region CM19Lib events
        
        private void Cm19LibOnRfSecurityReceived(object sender, CM19Lib.Events.RfSecurityReceivedEventArgs args)
        {
            ModuleTypes moduleType; string address;
            ParseSecurityModuleAddress(args.Address.ToString("X6"), args.Event.ToString(), out moduleType, out address);
            var module = GetSecurityModuleByAddress(address, moduleType);
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
            var module = standardModules.Find(m => m.Address == address);
            // ignore module if not belonging to monitored house codes
            if (module == null) return;;
            switch (args.Command)
            {
                case Function.On:
                    module.CustomData.Level = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Off:
                    module.CustomData.Level = 0D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Bright:
                    module.CustomData.Level += 1D/22D;
                    if (module.CustomData.Level > 1) module.CustomData.Level = 1D;
                    UpdateModuleLevel(module);
                    break;
                case Function.Dim:
                    module.CustomData.Level -= 1D/22D;
                    if (module.CustomData.Level < 0) module.CustomData.Level = 0D;
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
            ParseSecurityModuleAddress(args.Address.ToString("X6"), args.Event.ToString(), out moduleType, out address);
            var module = GetSecurityModuleByAddress(address, moduleType);
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
            // skip initial "5D-" (5D-29-BE-B1-26-D9-XX-XX)
            if (code.StartsWith("5D ")) code = code.Substring(3);
            OnInterfacePropertyChanged(this.GetDomain(), "RF", "X10 RF Receiver", ModuleEvents.Receiver_RawData, code);
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

    public class X10ModuleData
    {
        private double level = 0;
        public double Level {
            get => level;
            set
            {
                level = value;
                if (level != 0)
                {
                    LastLevel = level;
                }
            }
        }
        public double LastLevel;
        public ModuleTypes Type = ModuleTypes.Generic;
    }

}
