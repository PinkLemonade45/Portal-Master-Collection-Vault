using System.Security.Cryptography;

namespace SkylandersCollection.Desktop;

internal static class FigureStatsReader
{
    private const int BlockSize = 16;
    private const int BlockCount = 0x40;

    private static readonly byte[] KeySuffix =
    [
        0x20, 0x43, 0x6F, 0x70, 0x79, 0x72, 0x69, 0x67,
        0x68, 0x74, 0x20, 0x28, 0x43, 0x29, 0x20, 0x32,
        0x30, 0x31, 0x30, 0x20, 0x41, 0x63, 0x74, 0x69,
        0x76, 0x69, 0x73, 0x69, 0x6F, 0x6E, 0x2E, 0x20,
        0x41, 0x6C, 0x6C, 0x20, 0x52, 0x69, 0x67, 0x68,
        0x74, 0x73, 0x20, 0x52, 0x65, 0x73, 0x65, 0x72,
        0x76, 0x65, 0x64, 0x2E, 0x20
    ];

    public static FigureParsedStats? TryRead(IReadOnlyList<FigureBlockDump> blocks)
    {
        byte[][] rawBlocks = new byte[BlockCount][];
        foreach (FigureBlockDump block in blocks)
        {
            if (!block.Success || block.Block is < 0 or >= BlockCount)
            {
                continue;
            }

            byte[]? data = ParseHex(block.Data);
            if (data?.Length == BlockSize)
            {
                rawBlocks[block.Block] = data;
            }
        }

        if (rawBlocks[0] is null || rawBlocks[1] is null)
        {
            return null;
        }

        for (int block = 0; block < BlockCount; block++)
        {
            rawBlocks[block] ??= new byte[BlockSize];
        }

        byte[][] decryptedBlocks = rawBlocks.Select(block => block.ToArray()).ToArray();
        for (int block = 0x08; block < BlockCount; block++)
        {
            if (!IsAccessControlBlock(block))
            {
                DecryptBlock(decryptedBlocks[block], block, rawBlocks[0], rawBlocks[1]);
            }
        }

        int area0Sequence = decryptedBlocks[0x08][0x09];
        int area1Sequence = decryptedBlocks[0x24][0x09];
        int activeArea = IsNewerSequence(area1Sequence, area0Sequence) ? 1 : 0;
        int areaBlock = activeArea == 0 ? 0x08 : 0x24;
        int baseXp = ReadUInt16(decryptedBlocks[areaBlock], 0x00);
        int gold = ReadUInt16(decryptedBlocks[areaBlock], 0x03);
        int skill = ReadUInt16(decryptedBlocks[areaBlock + 1], 0x00);
        int platformFlags = decryptedBlocks[areaBlock + 1][0x03];
        int hat = ReadUInt16(decryptedBlocks[areaBlock + 1], 0x04);
        int heroPoints = ReadUInt16(decryptedBlocks[areaBlock + 5], 0x0A);
        uint heroicChallenges = ReadUInt32(decryptedBlocks[areaBlock + 8], 0x0C);
        int activeExtendedArea = GetPrimaryExtendedArea(decryptedBlocks);
        int activeExtendedAreaBlock = activeExtendedArea == 2 ? 0x11 : 0x2D;
        int postLevel10Xp = ReadUInt16(decryptedBlocks[activeExtendedAreaBlock], 0x03);
        int postLevel15Xp = ReadUInt16(decryptedBlocks[activeExtendedAreaBlock], 0x08);
        int totalXp = GetTotalExperience(baseXp, postLevel10Xp, postLevel15Xp);

        return new FigureParsedStats
        {
            Level = EstimateLevel(totalXp),
            Experience = totalXp,
            BaseExperience = baseXp,
            PostLevel10Experience = postLevel10Xp,
            PostLevel15Experience = postLevel15Xp,
            Gold = gold,
            SkillBits = $"0x{skill:X4}",
            SkillPath = GetSkillPath(skill),
            PlatformFlags = $"0x{platformFlags:X2}",
            HatId = hat,
            Nickname = ReadNickname(decryptedBlocks, areaBlock),
            HeroPoints = heroPoints,
            HeroicChallenges = $"0x{heroicChallenges:X8}"
        };
    }

    public static byte[]? TryDecryptBlock(string? block0Hex, string? block1Hex, byte[] encryptedBlock, int blockIndex)
    {
        byte[]? block0 = ParseHex(block0Hex);
        byte[]? block1 = ParseHex(block1Hex);
        if (block0?.Length != BlockSize || block1?.Length != BlockSize || encryptedBlock.Length != BlockSize)
        {
            return null;
        }

        byte[] decrypted = encryptedBlock.ToArray();
        DecryptBlock(decrypted, blockIndex, block0, block1);
        return decrypted;
    }

    private static void DecryptBlock(byte[] blockData, int blockIndex, byte[] block0, byte[] block1)
    {
        byte[] keyMaterial = new byte[0x56];
        Buffer.BlockCopy(block0, 0, keyMaterial, 0, BlockSize);
        Buffer.BlockCopy(block1, 0, keyMaterial, BlockSize, BlockSize);
        keyMaterial[0x20] = (byte)blockIndex;
        Buffer.BlockCopy(KeySuffix, 0, keyMaterial, 0x21, KeySuffix.Length);

        byte[] key = MD5.HashData(keyMaterial);
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        byte[] decrypted = decryptor.TransformFinalBlock(blockData, 0, BlockSize);
        Buffer.BlockCopy(decrypted, 0, blockData, 0, BlockSize);
    }

    private static bool IsAccessControlBlock(int blockIndex) => blockIndex % 4 == 3;

    private static byte[]? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string compact = new(text.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private static int ReadUInt16(byte[] data, int offset) =>
        data[offset] | data[offset + 1] << 8;

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] |
            data[offset + 1] << 8 |
            data[offset + 2] << 16 |
            data[offset + 3] << 24);

    private static string GetSkillPath(int skill)
    {
        if ((skill & 0x0001) == 0)
        {
            return "No Path";
        }

        return (skill & 0x0002) == 0
            ? "Path A"
            : "Path B";
    }

    private static int GetPrimaryExtendedArea(byte[][] blocks)
    {
        int area2Sequence = blocks[0x11][0x02];
        int area3Sequence = blocks[0x2D][0x02];
        return IsNewerSequence(area3Sequence, area2Sequence) ? 3 : 2;
    }

    private static bool IsNewerSequence(int candidate, int current)
    {
        if (candidate == current)
        {
            return false;
        }

        return (byte)(candidate - current) < 0x80;
    }

    private static string? ReadNickname(byte[][] blocks, int areaBlock)
    {
        List<char> chars = [];
        for (int i = 0; i < 15; i++)
        {
            int block = areaBlock + (i < 8 ? 2 : 4);
            int offset = (i * 2) & 0x0F;
            ushort value = (ushort)ReadUInt16(blocks[block], offset);
            if (value == 0)
            {
                break;
            }

            if (value < 0x20 || value > 0x7E)
            {
                return null;
            }

            chars.Add((char)value);
        }

        string nickname = new string(chars.ToArray()).Trim();
        return nickname.Length > 0 && nickname.Any(char.IsLetterOrDigit)
            ? nickname
            : null;
    }

    private static int EstimateLevel(int xp)
    {
        int[] thresholds =
        [
            0, 1000, 2200, 3800, 6000,
            9000, 13000, 18200, 24800, 33000,
            42700, 53900, 66600, 80800, 96500,
            113700, 132400, 152600, 174300, 197500,
            222200
        ];

        int level = 1;
        for (int i = 1; i < thresholds.Length; i++)
        {
            if (xp >= thresholds[i])
            {
                level = i + 1;
            }
        }

        return level;
    }

    private static int GetTotalExperience(int baseXp, int postLevel10Xp, int postLevel15Xp)
    {
        const int level10Threshold = 33000;
        const int level15Threshold = 96500;

        int totalXp = baseXp;
        if (baseXp >= level10Threshold)
        {
            totalXp += postLevel10Xp;
        }
        if (totalXp >= level15Threshold)
        {
            totalXp += postLevel15Xp;
        }

        return totalXp;
    }

}

internal sealed class FigureParsedStats
{
    public int Level { get; init; }
    public int Experience { get; init; }
    public int BaseExperience { get; init; }
    public int PostLevel10Experience { get; init; }
    public int PostLevel15Experience { get; init; }
    public int Gold { get; init; }
    public string? SkillBits { get; init; }
    public string? SkillPath { get; init; }
    public string? PlatformFlags { get; init; }
    public int HatId { get; init; }
    public string? Nickname { get; init; }
    public int HeroPoints { get; init; }
    public string? HeroicChallenges { get; init; }
}
