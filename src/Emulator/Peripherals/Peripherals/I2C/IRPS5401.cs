//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.I2C
{
    // Infineon IRPS5401 multi-phase PMBus power-stage IC. Board has three
    // instances on i2c0 (0x43/0x4d/0x4f, see zynqmp_enclustra_andromeda_xzu65.dtsi),
    // each bound by Linux's generic pmbus_core + CONFIG_SENSORS_IRPS5401 driver at boot.
    //
    // Implements only the PMBus command subset pmbus_core actually reads to populate
    // hwmon (PAGE, STATUS_BYTE/WORD, READ_VIN/VOUT/IOUT/TEMPERATURE_1, VOUT_MODE,
    // PMBUS_REVISION) with plausible fixed telemetry values - not real silicon
    // behavior (no fault injection, no real rail-to-rail variation), just enough
    // for the driver to bind cleanly without I/O errors instead of leaving the
    // node unprobed/erroring like an absent device would.
    public class IRPS5401 : II2CPeripheral
    {
        public IRPS5401()
        {
            Reset();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return;
            }

            command = (Command)data[0];
            // PMBus write-byte transactions (currently only PAGE) carry their
            // payload in the same message as the command code; read transactions
            // send just the command byte here, then a separate Read call follows
            // (SMBus repeated start) - Cadence_I2C keeps our command/page state
            // across that gap since it doesn't call FinishTransmission in between.
            if(command == Command.Page && data.Length > 1)
            {
                page = data[1];
            }
        }

        public byte[] Read(int count)
        {
            var response = GetResponse(command);
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = i < response.Length ? response[i] : (byte)0x00;
            }
            return result;
        }

        public void FinishTransmission()
        {
        }

        public void Reset()
        {
            page = 0;
            command = Command.Page;
        }

        private byte[] GetResponse(Command cmd)
        {
            switch(cmd)
            {
            case Command.Page:
                return new byte[] { page };
            case Command.StatusByte:
                return new byte[] { 0x00 };
            case Command.StatusWord:
                return new byte[] { 0x00, 0x00 };
            case Command.VoutMode:
                // Linear16 mode, fixed exponent -9 (mantissa/512) - a common
                // default across PMBus multi-phase controllers, not verified
                // against IRPS5401's actual factory-programmed exponent.
                return new byte[] { VoutModeByte };
            case Command.ReadVin:
                return Linear11(12.0, -3);
            case Command.ReadVout:
                return Linear16(RailVoltage(page));
            case Command.ReadIout:
                return Linear11(2.0, -4);
            case Command.ReadTemperature1:
                return Linear11(40.0, 0);
            case Command.PmbusRevision:
                return new byte[] { 0x22 };
            default:
                this.Log(LogLevel.Debug, "Unhandled PMBus command 0x{0:X} on page {1}, returning zero.", (byte)cmd, page);
                return new byte[] { 0x00 };
            }
        }

        // Fabricated per-rail voltage so different pages don't all read back
        // identically; not tied to any real board rail assignment.
        private static double RailVoltage(byte pageNumber)
        {
            switch(pageNumber)
            {
            case 0:
                return 0.85;
            case 1:
                return 1.2;
            case 2:
                return 1.8;
            case 3:
                return 2.5;
            default:
                return 3.3;
            }
        }

        private static byte[] Linear11(double value, int exponent)
        {
            var mantissa = (int)Math.Round(value / Math.Pow(2, exponent)) & MantissaMask11;
            var raw = ((exponent & ExponentMask5) << 11) | mantissa;
            return new byte[] { (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF) };
        }

        private static byte[] Linear16(double value)
        {
            var mantissa = (uint)Math.Round(value * 512.0) & 0xFFFF;
            return new byte[] { (byte)(mantissa & 0xFF), (byte)((mantissa >> 8) & 0xFF) };
        }

        private byte page;
        private Command command;

        private const byte VoutModeByte = 0x17;
        private const int MantissaMask11 = 0x7FF;
        private const int ExponentMask5 = 0x1F;

        private enum Command : byte
        {
            Page = 0x00,
            StatusByte = 0x78,
            StatusWord = 0x79,
            ReadVin = 0x88,
            ReadVout = 0x8B,
            ReadIout = 0x8C,
            ReadTemperature1 = 0x8D,
            VoutMode = 0x20,
            PmbusRevision = 0x98,
        }
    }
}
