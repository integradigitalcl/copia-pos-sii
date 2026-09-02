using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

const string activationId = "GF-WEB-E2E-6036";
var dataDirs = new[]
{
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrunflexPOS", "data"),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GrunflexPOS", "data")
};

using var rsa = RSA.Create(2048);
var privatePem = rsa.ExportRSAPrivateKeyPem();
var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

foreach (var dir in dataDirs)
{
    Directory.CreateDirectory(dir);
    UpdateSecrets(Path.Combine(dir, "api.secrets.json"), privatePem, publicPem);
    File.WriteAllText(Path.Combine(dir, "licensing-public.pem"), publicPem);
    SeedLicenseRecord(Path.Combine(dir, "grunflex_api.db"), activationId);
    Console.WriteLine($"OK {dir}");
}

Console.WriteLine($"ActivationId={activationId}");
Console.WriteLine($"Machine={Environment.MachineName}");

static void UpdateSecrets(string path, string privatePem, string publicPem)
{
    JsonObject root;
    if (File.Exists(path))
    {
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }
    }
    else
    {
        root = new JsonObject
        {
            ["Jwt"] = new JsonObject { ["SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) },
            ["Security"] = new JsonObject { ["AdminPassword"] = "demo-admin-e2e" }
        };
    }

    var licensing = root["Licensing"] as JsonObject ?? new JsonObject();
    licensing["PrivateKeyPem"] = privatePem;
    licensing["PublicKeyPem"] = publicPem;
    if (licensing["IssuerApiKey"] == null)
        licensing["IssuerApiKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    root["Licensing"] = licensing;

    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static void SeedLicenseRecord(string dbPath, string activationId)
{
    if (!File.Exists(dbPath))
        return;

    using var c = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
    c.Open();

    using (var exists = c.CreateCommand())
    {
        exists.CommandText = "SELECT 1 FROM LicenseIssuerRecords WHERE ActivationId = $aid LIMIT 1";
        exists.Parameters.AddWithValue("$aid", activationId);
        if (exists.ExecuteScalar() is not null)
            return;
    }

    var exp = DateTime.UtcNow.AddDays(365).ToString("O");
    using var ins = c.CreateCommand();
    ins.CommandText = """
        INSERT INTO LicenseIssuerRecords
        (Id, ActivationId, CustomerName, BusinessName, LicenseType, NumberOfBoxes, ExpUtc,
         Multicaja, OnlineSupport, CloudBackup, PrioritySupport, OfflineGraceDays, LicenseToken, CreatedAtUtc)
        VALUES ($id, $aid, $customer, $business, $type, $boxes, $exp,
                1, 1, 1, 0, 14, '', $created)
        """;
    ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
    ins.Parameters.AddWithValue("$aid", activationId);
    ins.Parameters.AddWithValue("$customer", "Cliente Web E2E");
    ins.Parameters.AddWithValue("$business", "Minimarket Demo");
    ins.Parameters.AddWithValue("$type", "Pro+Multicaja");
    ins.Parameters.AddWithValue("$boxes", 5);
    ins.Parameters.AddWithValue("$exp", exp);
    ins.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
    ins.ExecuteNonQuery();
}
