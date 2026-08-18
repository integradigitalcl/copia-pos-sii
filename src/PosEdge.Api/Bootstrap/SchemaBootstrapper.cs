using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace PosEdge.Api.Bootstrap;

public static class SchemaBootstrapper
{
    public static async Task ApplySchemaAndSeedAsync(IConfiguration cfg, ILogger logger, CancellationToken ct)
    {
        var auto = cfg.GetValue("Edge:AutoApplySchema", false);
        if (!auto)
            return;

        var cs = cfg.GetConnectionString("PosEdge");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:PosEdge requerido para AutoApplySchema.");

        var schemaPath = cfg["Edge:SchemaPath"];
        var seedPath = cfg["Edge:SeedPath"];
        var autoSeed = cfg.GetValue("Edge:AutoSeed", false);

        if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
            throw new FileNotFoundException("SchemaPath no encontrado.", schemaPath);

        var schemaSql = await File.ReadAllTextAsync(schemaPath, ct).ConfigureAwait(false);
        var schemaPreview = schemaSql.Length <= 200 ? schemaSql : schemaSql[..200];
        string? seedSql = null;
        if (autoSeed && !string.IsNullOrWhiteSpace(seedPath) && File.Exists(seedPath))
            seedSql = await File.ReadAllTextAsync(seedPath, ct).ConfigureAwait(false);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        logger.LogInformation("Applying schema from {SchemaPath}", schemaPath);

        // Apply statement-by-statement so we can pinpoint failures (esp. DO $$ blocks and incremental schema changes).
        var statements = SplitSqlStatements(schemaSql);
        for (var i = 0; i < statements.Count; i++)
        {
            var st = statements[i];
            if (string.IsNullOrWhiteSpace(st))
                continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = st;
            cmd.CommandTimeout = 60;
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex)
            {
                var head = st.Length <= 200 ? st : st[..200];
                logger.LogError(ex,
                    "Schema apply failed at statement #{Index}/{Total}. SqlState={SqlState} Position={Position} Where={Where}. Head={Head}. FilePreview={Preview}",
                    i + 1, statements.Count, ex.SqlState, ex.Position, ex.Where,
                    head.Replace("\r", "\\r").Replace("\n", "\\n"),
                    schemaPreview.Replace("\r", "\\r").Replace("\n", "\\n"));
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(seedSql))
        {
            await using var seedCmd = conn.CreateCommand();
            seedCmd.CommandText = seedSql;
            seedCmd.CommandTimeout = 60;
            logger.LogInformation("Seeding from {SeedPath}", seedPath);
            await seedCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static List<string> SplitSqlStatements(string sql)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;
        string? dollarTag = null; // "$$" or "$tag$"

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // Handle line comments (--) when not in quotes/dollar-quote
            if (!inSingle && !inDouble && dollarTag == null && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                // consume until newline
                sb.Append(c);
                sb.Append(sql[i + 1]);
                i += 2;
                while (i < sql.Length)
                {
                    var cc = sql[i];
                    sb.Append(cc);
                    if (cc == '\n')
                        break;
                    i++;
                }
                continue;
            }

            // Dollar-quoted blocks: detect start/end
            if (!inSingle && !inDouble)
            {
                if (dollarTag == null && c == '$')
                {
                    // Try parse a dollar tag: $tag$
                    var j = i + 1;
                    while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_'))
                        j++;
                    if (j < sql.Length && sql[j] == '$')
                    {
                        dollarTag = sql.Substring(i, (j - i) + 1);
                        sb.Append(dollarTag);
                        i = j;
                        continue;
                    }
                }
                else if (dollarTag != null && c == '$')
                {
                    // check for end tag match
                    if (i + dollarTag.Length <= sql.Length && string.CompareOrdinal(sql, i, dollarTag, 0, dollarTag.Length) == 0)
                    {
                        sb.Append(dollarTag);
                        i += dollarTag.Length - 1;
                        dollarTag = null;
                        continue;
                    }
                }
            }

            if (dollarTag == null)
            {
                if (!inDouble && c == '\'' )
                {
                    // toggle single quote; handle escaped '' inside string by peeking next char
                    if (inSingle && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        sb.Append("''");
                        i++;
                        continue;
                    }
                    inSingle = !inSingle;
                }
                else if (!inSingle && c == '"')
                {
                    inDouble = !inDouble;
                }
            }

            if (c == ';' && !inSingle && !inDouble && dollarTag == null)
            {
                list.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
            list.Add(sb.ToString());
        return list;
    }
}

