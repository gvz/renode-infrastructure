//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.I2C
{
    // Atmel/Microchip ATSHA204A crypto-authentication chip, at i2c0/0x64 on this
    // board (zynqmp_enclustra_andromeda_xzu65.dtsi). Bound at boot by Linux's
    // CONFIG_CRYPTO_DEV_ATMEL_SHA204A (drivers/crypto/atmel-sha204a.c, registers
    // as a hwrng) and, before that, by U-Boot's atsha204a_wakeup() during
    // enclustra_common() init - see qemu-user.dtsi's comment on why the QEMU
    // machine deletes this node instead of modeling it (their I2C model
    // reportedly doesn't NAK a missing device there, hanging U-Boot). Renode's
    // Cadence_I2C here does correctly NAK a missing target (see OnStateChange's
    // targetDevice == null branch), so unlike QEMU we can just model the real
    // chip instead of needing to delete the DT node.
    //
    // This isn't a full crypto-auth implementation (no EEPROM config/OTP/data
    // zone persistence, no MAC/HMAC/GenDig, no lock state machine) - it's the
    // minimum wire protocol surface the kernel driver actually exercises:
    // the wake sequence, and the Random and Read opcodes, framed and CRC'd
    // exactly like the real device so the driver's own response validation
    // (atmel_i2c_checksum in atmel-i2c.c) passes instead of erroring out.
    public class ATSHA204A : II2CPeripheral
    {
        public ATSHA204A()
        {
            Reset();
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return;
            }

            var wordAddress = (WordAddress)data[0];
            switch(wordAddress)
            {
            case WordAddress.Reset:
            case WordAddress.Idle:
            case WordAddress.Sleep:
                // Real device: these return to idle/sleep with no response
                // pending. We still leave a wake-token as the next read's
                // fallback, matching the state right after a real wake pulse -
                // callers that immediately re-wake and read will get a sane
                // reply rather than stale command output.
                pendingResponse = WakeToken;
                break;
            case WordAddress.Command:
                pendingResponse = HandleCommand(data);
                break;
            default:
                this.Log(LogLevel.Warning, "Unknown ATSHA204A word address 0x{0:X}.", data[0]);
                pendingResponse = WakeToken;
                break;
            }
        }

        public byte[] Read(int count)
        {
            var response = pendingResponse ?? WakeToken;
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
            // A bare read with no preceding Write (the actual wake sequence:
            // a NACK'd write to the general-call address, then a plain read of
            // the target device) lands here directly, so the wake token must
            // already be the default pending response after any reset.
            pendingResponse = WakeToken;
        }

        private byte[] HandleCommand(byte[] data)
        {
            // Packet layout after the word-address byte: count, opcode, param1,
            // param2(2), data[], crc(2). count includes itself and the CRC.
            if(data.Length < 8)
            {
                this.Log(LogLevel.Warning, "ATSHA204A command packet too short ({0} bytes).", data.Length);
                return StatusResponse(ExecutionError);
            }

            var opcode = (Opcode)data[2];
            switch(opcode)
            {
            case Opcode.Random:
                return RandomResponse();
            case Opcode.Read:
                return ReadZoneResponse(data[3]);
            default:
                this.Log(LogLevel.Debug, "Unhandled ATSHA204A opcode 0x{0:X}, returning generic success status.", (byte)opcode);
                return StatusResponse(Success);
            }
        }

        private byte[] RandomResponse()
        {
            var payload = new byte[RandomPayloadLength];
            EmulationManager.Instance.CurrentEmulation.RandomGenerator.NextBytes(payload);
            return Frame(payload);
        }

        private byte[] ReadZoneResponse(byte zone)
        {
            // Bit 7 of the zone byte selects 32-byte (set) vs 4-byte (clear) reads.
            var length = (zone & 0x80) != 0 ? 32 : 4;
            // Fabricated OTP/config contents (not the real factory-programmed
            // serial/MAC) - enough for the driver to get a well-formed,
            // correctly-CRC'd response instead of an I/O error.
            var payload = new byte[length];
            return Frame(payload);
        }

        private static byte[] StatusResponse(byte status)
        {
            return Frame(new byte[] { status });
        }

        // count/payload/crc16, matching the real device's response packet format.
        private static byte[] Frame(byte[] payload)
        {
            var count = (byte)(payload.Length + 3);
            var framed = new byte[count];
            framed[0] = count;
            Array.Copy(payload, 0, framed, 1, payload.Length);
            var crc = Crc16(framed, count - 2);
            framed[count - 2] = crc[0];
            framed[count - 1] = crc[1];
            return framed;
        }

        // Atmel/Microchip's documented CRC16 (poly 0x8005, LSB-first bit order) -
        // same algorithm as Linux's atmel_i2c_checksum(), which the kernel driver
        // uses to validate every response we send it.
        private static byte[] Crc16(byte[] data, int length)
        {
            ushort crcRegister = 0;
            for(var counter = 0; counter < length; counter++)
            {
                for(byte shiftRegister = 0x01; shiftRegister != 0x00; shiftRegister <<= 1)
                {
                    var dataBit = (data[counter] & shiftRegister) != 0 ? 1 : 0;
                    var crcBit = (crcRegister >> 15) & 1;
                    crcRegister <<= 1;
                    if(dataBit != crcBit)
                    {
                        crcRegister ^= Polynomial;
                    }
                }
            }
            return new byte[] { (byte)(crcRegister & 0xFF), (byte)(crcRegister >> 8) };
        }

        private byte[] pendingResponse;

        private const ushort Polynomial = 0x8005;
        private const int RandomPayloadLength = 32;
        private const byte Success = 0x00;
        private const byte ExecutionError = 0x0F;

        // Fixed wake response the datasheet/driver expects after a successful
        // wake pulse - not chip-instance-specific, identical on every real part.
        private static readonly byte[] WakeToken = { 0x04, 0x11, 0x33, 0x43 };

        private enum WordAddress : byte
        {
            Reset = 0x00,
            Sleep = 0x01,
            Idle = 0x02,
            Command = 0x03,
        }

        private enum Opcode : byte
        {
            Read = 0x02,
            Random = 0x1B,
        }
    }
}
