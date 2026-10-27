using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace DuoTrack.Relay;

internal enum RegistrationResult
{
    Created,
    AlreadyExists,
    KeyConflict,
}

internal sealed record DeviceRecord(Guid Id, string Name, string PublicKey);

internal sealed class DeviceRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Devices (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                PublicKey TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Pairings (
                DeviceA TEXT NOT NULL,
                DeviceB TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                PRIMARY KEY (DeviceA, DeviceB)
            );
            CREATE TABLE IF NOT EXISTS PairCodes (
                Code TEXT PRIMARY KEY,
                CreatorId TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<RegistrationResult> RegisterDeviceAsync(Guid id, string name, string publicKey)
    {
        await using var connection = Open();
        var existing = await GetDeviceAsync(connection, id);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(existing.PublicKey),
                    Convert.FromBase64String(publicKey)))
            {
                return RegistrationResult.KeyConflict;
            }

            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Devices SET Name = $name WHERE Id = $id";
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$id", id.ToString("D"));
            await update.ExecuteNonQueryAsync();
            return RegistrationResult.AlreadyExists;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Devices (Id, Name, PublicKey, CreatedUtc) VALUES ($id, $name, $key, $created)";
        insert.Parameters.AddWithValue("$id", id.ToString("D"));
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$key", publicKey);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();
        return RegistrationResult.Created;
    }

    public async Task<bool> DeviceExistsAsync(Guid id) => await GetDeviceAsync(id) is not null;

    public async Task<DeviceRecord?> GetDeviceAsync(Guid id)
    {
        await using var connection = Open();
        return await GetDeviceAsync(connection, id);
    }

    public async Task<IReadOnlyList<DeviceRecord>> GetPairedDevicesAsync(Guid id)
    {
        var result = new List<DeviceRecord>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.Id, d.Name, d.PublicKey
            FROM Pairings p
            JOIN Devices d ON d.Id = CASE WHEN p.DeviceA = $id THEN p.DeviceB ELSE p.DeviceA END
            WHERE p.DeviceA = $id OR p.DeviceB = $id
            ORDER BY d.Name
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new DeviceRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    public async Task<bool> ArePairedAsync(Guid first, Guid second)
    {
        var (a, b) = NormalizePair(first, second);
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Pairings WHERE DeviceA = $a AND DeviceB = $b LIMIT 1";
        command.Parameters.AddWithValue("$a", a);
        command.Parameters.AddWithValue("$b", b);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task<string> CreatePairingCodeAsync(Guid creatorId)
    {
        await using var connection = Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM PairCodes WHERE CreatorId = $id OR ExpiresUtc <= $now";
            delete.Parameters.AddWithValue("$id", creatorId.ToString("D"));
            delete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await delete.ExecuteNonQueryAsync();
        }

        string code;
        while (true)
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            try
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = "INSERT INTO PairCodes (Code, CreatorId, ExpiresUtc) VALUES ($code, $id, $expires)";
                insert.Parameters.AddWithValue("$code", code);
                insert.Parameters.AddWithValue("$id", creatorId.ToString("D"));
                insert.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
                await insert.ExecuteNonQueryAsync();
                break;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
            }
        }

        await transaction.CommitAsync();
        return code;
    }

    public async Task<DeviceRecord?> JoinPairingAsync(Guid joiningId, string code)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        await using var connection = Open();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid creatorId;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText = "SELECT CreatorId, ExpiresUtc FROM PairCodes WHERE Code = $code";
            lookup.Parameters.AddWithValue("$code", code);
            await using var reader = await lookup.ExecuteReaderAsync();
            if (!await reader.ReadAsync() ||
                !DateTimeOffset.TryParse(reader.GetString(1), out var expires) || expires <= DateTimeOffset.UtcNow ||
                !Guid.TryParse(reader.GetString(0), out creatorId) || creatorId == joiningId)
            {
                return null;
            }
        }

        var (a, b) = NormalizePair(creatorId, joiningId);
        await using (var pair = connection.CreateCommand())
        {
            pair.Transaction = (SqliteTransaction)transaction;
            pair.CommandText = "INSERT OR IGNORE INTO Pairings (DeviceA, DeviceB, CreatedUtc) VALUES ($a, $b, $created)";
            pair.Parameters.AddWithValue("$a", a);
            pair.Parameters.AddWithValue("$b", b);
            pair.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await pair.ExecuteNonQueryAsync();
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM PairCodes WHERE Code = $code";
            delete.Parameters.AddWithValue("$code", code);
            await delete.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return await GetDeviceAsync(creatorId);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static async Task<DeviceRecord?> GetDeviceAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, PublicKey FROM Devices WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new DeviceRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static (string A, string B) NormalizePair(Guid first, Guid second)
    {
        var a = first.ToString("D");
        var b = second.ToString("D");
        return string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    }
}
