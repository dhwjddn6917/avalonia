namespace MobileEssControl.Services.Modbus;

public static class ModbusCrc16
{
    public static ushort Calculate(byte[] data, int length)
    {
        ushort crc = 0xFFFF;

        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];

            for (int bit = 0; bit < 8; bit++)
            {
                bool isLsbSet = (crc & 0x0001) != 0;

                crc >>= 1;

                if (isLsbSet)
                {
                    crc ^= 0xA001;
                }
            }
        }

        return crc;
    }

    public static byte[] AddCrc(byte[] frameWithoutCrc)
    {
        ushort crc = Calculate(
            frameWithoutCrc,
            frameWithoutCrc.Length);

        byte[] result = new byte[frameWithoutCrc.Length + 2];

        System.Array.Copy(
            frameWithoutCrc,
            result,
            frameWithoutCrc.Length);

        // Modbus RTU CRC는 Low Byte → High Byte 순서
        result[^2] = (byte)(crc & 0xFF);
        result[^1] = (byte)(crc >> 8);

        return result;
    }

    public static bool IsValid(byte[] frame)
    {
        if (frame.Length < 4)
        {
            return false;
        }

        ushort calculatedCrc = Calculate(
            frame,
            frame.Length - 2);

        ushort receivedCrc = (ushort)(
            frame[^2] |
            (frame[^1] << 8));

        return calculatedCrc == receivedCrc;
    }
}