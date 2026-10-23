using System.Data;
using System.Globalization;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace CouplesCash.Api.Data;

public static class DapperConfig
{
    private static int _configured;

    /// <summary>snake_case → PascalCase mapping and DateOnly ⇄ date support. Safe to call more than once.</summary>
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly"),
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.Value = value;
            if (parameter is NpgsqlParameter np)
            {
                np.NpgsqlDbType = NpgsqlDbType.Date;
            }
        }
    }
}
