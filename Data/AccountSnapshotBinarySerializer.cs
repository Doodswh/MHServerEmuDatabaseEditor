using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MHDBEditor.Models;

namespace MHDBEditor.Data
{
    // Wire-compatible port of MHServerEmu.DatabaseAccess.Json.DBAccountBinarySerializer
    // (marked "Experimental" in the original source) - same magic bytes, same version byte,
    // same field order/sizes, operating on DBAccountSnapshot instead of the server's own
    // DBAccount/DBEntityCollection object graph. A file this writes has the identical byte
    // layout the original class would produce for the same account, and vice versa.
    //
    // One quirk carried over faithfully from the original: WriteDBPlayer/ReadDBPlayer only
    // cover DbGuid/ArchiveData/StartTarget/AOIVolume - GazillioniteBalance and LastLogoutTime
    // are NOT part of this format. Importing a snapshot resets those two fields on the target
    // Player row to their defaults (-1 and 0), same as the original would.
    //
    // Unlike the original (which catches exceptions internally, logs, and returns null), this
    // port lets exceptions propagate so the caller (the GUI) can show them directly - this is
    // purely an error-handling difference and does not affect the byte format at all.
    public static class AccountSnapshotBinarySerializer
    {
        private const string Magic = "534156"; // "SAV" - matches the original exactly
        private const byte SerializerVersion = 1;

        public static void SerializeToFile(string path, DBAccountSnapshot snapshot)
        {
            using FileStream fs = new(path, FileMode.Create);
            Serialize(fs, snapshot);
        }

        public static DBAccountSnapshot DeserializeFromFile(string path)
        {
            using FileStream fs = new(path, FileMode.Open);
            return Deserialize(fs);
        }

        public static void Serialize(Stream stream, DBAccountSnapshot snapshot)
        {
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

            WriteFileHeader(writer);

            DBAccount account = snapshot.Account;
            writer.Write(account.Id);
            WriteString(writer, account.Email ?? string.Empty);
            WriteString(writer, account.PlayerName ?? string.Empty);
            WriteByteArray(writer, account.PasswordHash ?? Array.Empty<byte>());
            WriteByteArray(writer, account.Salt ?? Array.Empty<byte>());
            writer.Write((byte)account.UserLevel);
            writer.Write((int)account.Flags);

            WriteDBPlayer(writer, snapshot.Player);

            WriteDBEntityList(writer, snapshot.Avatars);
            WriteDBEntityList(writer, snapshot.TeamUps);
            WriteDBEntityList(writer, snapshot.Items);
            WriteDBEntityList(writer, snapshot.ControlledEntities);
        }

        public static DBAccountSnapshot Deserialize(Stream stream)
        {
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

            if (ReadFileHeader(reader) == false)
                throw new InvalidDataException("File header is missing or uses an unsupported version.");

            DBAccount account = new()
            {
                Id = reader.ReadInt64(),
                Email = ReadString(reader),
                PlayerName = ReadString(reader),
                PasswordHash = ReadByteArray(reader),
                Salt = ReadByteArray(reader),
                UserLevel = (AccountUserLevel)reader.ReadByte(),
                Flags = (AccountFlags)reader.ReadInt32(),
            };

            DBAccountSnapshot snapshot = new() { Account = account };

            snapshot.Player = ReadDBPlayer(reader);
            snapshot.Player.DbGuid = account.Id; // Player.DbGuid always mirrors the account id

            snapshot.Avatars = ReadDBEntityList(reader);
            snapshot.TeamUps = ReadDBEntityList(reader);
            snapshot.Items = ReadDBEntityList(reader);
            snapshot.ControlledEntities = ReadDBEntityList(reader);

            return snapshot;
        }

        private static void WriteFileHeader(BinaryWriter writer)
        {
            writer.Write(Convert.FromHexString(Magic));
            writer.Write(SerializerVersion);
        }

        private static bool ReadFileHeader(BinaryReader reader)
        {
            string magic = Convert.ToHexString(reader.ReadBytes(3));
            if (magic != Magic)
                return false;

            byte version = reader.ReadByte();
            return version == SerializerVersion;
        }

        private static void WriteByteArray(BinaryWriter writer, byte[] array)
        {
            if (array.Length > ushort.MaxValue)
                throw new InvalidDataException($"A {array.Length:N0}-byte blob is too big for the .sav format (64 KB max per entry, same as the server's account serializer). Back up the database file instead.");
            writer.Write((ushort)array.Length);
            writer.Write(array);
        }

        private static byte[] ReadByteArray(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            return reader.ReadBytes(length);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            WriteByteArray(writer, Encoding.UTF8.GetBytes(value));
        }

        private static string ReadString(BinaryReader reader)
        {
            return Encoding.UTF8.GetString(ReadByteArray(reader));
        }

        private static void WriteDBPlayer(BinaryWriter writer, DBPlayer dbPlayer)
        {
            dbPlayer ??= new DBPlayer();

            writer.Write(dbPlayer.DbGuid);
            WriteByteArray(writer, dbPlayer.ArchiveData ?? Array.Empty<byte>());
            writer.Write(dbPlayer.StartTarget);
            writer.Write(dbPlayer.AOIVolume);
        }

        private static DBPlayer ReadDBPlayer(BinaryReader reader)
        {
            return new DBPlayer
            {
                DbGuid = reader.ReadInt64(),
                ArchiveData = ReadByteArray(reader),
                StartTarget = reader.ReadInt64(),
                AOIVolume = reader.ReadInt32(),
            };
        }

        private static void WriteDBEntityList(BinaryWriter writer, List<DBEntity> entities)
        {
            if (entities.Count > ushort.MaxValue) throw new OverflowException();

            writer.Write((ushort)entities.Count);
            foreach (DBEntity entity in entities)
                WriteDBEntity(writer, entity);
        }

        private static List<DBEntity> ReadDBEntityList(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            List<DBEntity> entities = new(count);
            for (int i = 0; i < count; i++)
                entities.Add(ReadDBEntity(reader));
            return entities;
        }

        private static void WriteDBEntity(BinaryWriter writer, DBEntity entity)
        {
            writer.Write(entity.DbGuid);
            writer.Write(entity.ContainerDbGuid);
            writer.Write(entity.InventoryProtoGuid);
            writer.Write(entity.Slot);
            writer.Write(entity.EntityProtoGuid);
            WriteByteArray(writer, entity.ArchiveData ?? Array.Empty<byte>());
        }

        private static DBEntity ReadDBEntity(BinaryReader reader)
        {
            return new DBEntity
            {
                DbGuid = reader.ReadInt64(),
                ContainerDbGuid = reader.ReadInt64(),
                InventoryProtoGuid = reader.ReadInt64(),
                Slot = reader.ReadUInt32(),
                EntityProtoGuid = reader.ReadInt64(),
                ArchiveData = ReadByteArray(reader),
            };
        }
    }
}
