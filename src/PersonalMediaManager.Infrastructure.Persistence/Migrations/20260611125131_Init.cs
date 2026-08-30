using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PersonalMediaManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit_ScheduledTaskRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FireInstanceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Running"),
                    ProcessedCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DetailJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit_ScheduledTaskRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Category_Definition",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetRoot = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category_Definition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Media_Company",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LogoPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OriginCountry = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Company", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Media_Genre",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Genre", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Media_Keyword",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Keyword", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Media_Network",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LogoPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OriginCountry = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Network", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Media_Person",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProfilePath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    KnownForDepartment = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Person", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Parse_AiProvider",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CostTier = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Paid"),
                    StructuredJson = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    ConfidenceThreshold = table.Column<double>(type: "REAL", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    DisabledUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    ExtraOptions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UseProxy = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parse_AiProvider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Parse_Rule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "FileName"),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DefaultType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ForceType = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    ConfidenceBonus = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.0),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parse_Rule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Parse_TestCase",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Manual"),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "PendingTriage"),
                    SamplePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    WatchRootPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ExpectedTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ExpectedYear = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedMediaType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    ExpectedSeason = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "NotRun"),
                    LastRunResult = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastMatchedRuleId = table.Column<long>(type: "INTEGER", nullable: true),
                    AiVerdict = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AiSuggestedRulePattern = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parse_TestCase", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "System_MediaExtension",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_System_MediaExtension", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "System_Setting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "General"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_System_Setting", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tmdb_MetadataCache",
                columns: table => new
                {
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalSeasons = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OriginCountry = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OriginalLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Genres = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    CachedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tmdb_MetadataCache", x => new { x.TmdbId, x.MediaType });
                });

            migrationBuilder.CreateTable(
                name: "Tmdb_SearchCache",
                columns: table => new
                {
                    QueryHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QueryRaw = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Results = table.Column<string>(type: "TEXT", nullable: false),
                    CachedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tmdb_SearchCache", x => x.QueryHash);
                });

            migrationBuilder.CreateTable(
                name: "Tmdb_Setting",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "zh-CN"),
                    FallbackLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "en-US"),
                    CandidateThreshold = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 3),
                    RateLimitPerSecond = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 40),
                    MetadataCacheHours = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 24),
                    SearchCacheMinutes = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 60),
                    ScoreWeightTitle = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.5),
                    ScoreWeightYear = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.29999999999999999),
                    ScoreWeightPopularity = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.10000000000000001),
                    ScoreWeightLanguage = table.Column<double>(type: "REAL", nullable: false, defaultValue: 0.10000000000000001),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tmdb_Setting", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User_Account",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, defaultValue: "Viewer"),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastLoginIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User_Account", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watch_Folder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsTransit = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsNetworkShare = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    LastScanAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastReachableAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watch_Folder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watch_IgnoreRule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watch_IgnoreRule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Webhook_Subscription",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SecretEncrypted = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Events = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: "[]"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhook_Subscription", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Category_MatchRule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Conditions = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false, defaultValue: "{}"),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 100),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category_MatchRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Category_MatchRule_Category_Definition_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Category_Definition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_Item",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourcePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ParseSource = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    ParsedInfo = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbMediaType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    TmdbCandidatesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FileMissing = table.Column<bool>(type: "INTEGER", nullable: false),
                    FileCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Item_Category_Definition_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Category_Definition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Media_Work",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Tagline = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BackdropPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    VoteAverage = table.Column<double>(type: "REAL", nullable: true),
                    VoteCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<long>(type: "INTEGER", nullable: true),
                    TmdbStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OriginalLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    OriginCountry = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Homepage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TotalSeasons = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalEpisodes = table.Column<int>(type: "INTEGER", nullable: true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    EnrichedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Work", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Work_Category_Definition_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Category_Definition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Audit_Operation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit_Operation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Audit_Operation_User_Account_UserId",
                        column: x => x.UserId,
                        principalTable: "User_Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Webhook_Delivery",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubscriptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Event = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastTriedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextRetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhook_Delivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Webhook_Delivery_Webhook_Subscription_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Webhook_Subscription",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Audit_AiCall",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<long>(type: "INTEGER", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ErrorType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ErrorDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ChainId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AttemptLevel = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    RequestText = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseText = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit_AiCall", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Audit_AiCall_Media_Item_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Media_Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Audit_AiCall_Parse_AiProvider_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "Parse_AiProvider",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Process_Step",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    Stage = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurMs = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Process_Step", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Process_Step_Media_Item_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "Media_Item",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_Episode",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    StillPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AirDate = table.Column<long>(type: "INTEGER", nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    VoteAverage = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Episode", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Episode_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_Season",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    PosterPath = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AirDate = table.Column<long>(type: "INTEGER", nullable: true),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_Season", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Season_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_WorkCompany",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_WorkCompany", x => new { x.WorkId, x.CompanyId });
                    table.ForeignKey(
                        name: "FK_Media_WorkCompany_Media_Company_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Media_Company",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_WorkCompany_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_WorkCredit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreditType = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Character = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Ord = table.Column<int>(type: "INTEGER", nullable: true),
                    Job = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Department = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_WorkCredit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_WorkCredit_Media_Person_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Media_Person",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_WorkCredit_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_WorkGenre",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    GenreId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_WorkGenre", x => new { x.WorkId, x.GenreId });
                    table.ForeignKey(
                        name: "FK_Media_WorkGenre_Media_Genre_GenreId",
                        column: x => x.GenreId,
                        principalTable: "Media_Genre",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_WorkGenre_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_WorkKeyword",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    KeywordId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_WorkKeyword", x => new { x.WorkId, x.KeywordId });
                    table.ForeignKey(
                        name: "FK_Media_WorkKeyword_Media_Keyword_KeywordId",
                        column: x => x.KeywordId,
                        principalTable: "Media_Keyword",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_WorkKeyword_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Media_WorkNetwork",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media_WorkNetwork", x => new { x.WorkId, x.NetworkId });
                    table.ForeignKey(
                        name: "FK_Media_WorkNetwork_Media_Network_NetworkId",
                        column: x => x.NetworkId,
                        principalTable: "Media_Network",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Media_WorkNetwork_Media_Work_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Media_Work",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "System_MediaExtension",
                columns: new[] { "Id", "CreatedAt", "Description", "Enabled", "Extension", "UpdatedAt" },
                values: new object[,]
                {
                    { 1L, 1308995223552000000L, "内置默认", true, ".mkv", 1308995223552000000L },
                    { 2L, 1308995223552000000L, "内置默认", true, ".mp4", 1308995223552000000L },
                    { 3L, 1308995223552000000L, "内置默认", true, ".avi", 1308995223552000000L },
                    { 4L, 1308995223552000000L, "内置默认", true, ".mov", 1308995223552000000L },
                    { 5L, 1308995223552000000L, "内置默认", true, ".wmv", 1308995223552000000L },
                    { 6L, 1308995223552000000L, "内置默认", true, ".flv", 1308995223552000000L },
                    { 7L, 1308995223552000000L, "内置默认", true, ".webm", 1308995223552000000L },
                    { 8L, 1308995223552000000L, "内置默认", true, ".m4v", 1308995223552000000L },
                    { 9L, 1308995223552000000L, "内置默认", true, ".ts", 1308995223552000000L },
                    { 10L, 1308995223552000000L, "内置默认", true, ".mpg", 1308995223552000000L },
                    { 11L, 1308995223552000000L, "内置默认", true, ".mpeg", 1308995223552000000L },
                    { 12L, 1308995223552000000L, "内置默认", true, ".m2ts", 1308995223552000000L },
                    { 13L, 1308995223552000000L, "内置默认", true, ".rmvb", 1308995223552000000L },
                    { 14L, 1308995223552000000L, "内置默认", true, ".rm", 1308995223552000000L }
                });

            migrationBuilder.InsertData(
                table: "System_Setting",
                columns: new[] { "Key", "Category", "CreatedAt", "Description", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { "Archive_DiskCriticalPercent", "Archive", 1309006725120000000L, "归档盘剩余空间低于此百分比 → 健康检查 fail + disk.low 严重通知（0=不检查）", 1309006725120000000L, "5" },
                    { "Archive_DiskWarnPercent", "Archive", 1309006725120000000L, "归档盘剩余空间低于此百分比 → 健康检查 warn + disk.low 通知（0=不检查）", 1309006725120000000L, "10" },
                    { "Audit_AiCallMaxRowsPerProvider", "Audit", 1309002301440000000L, "单 AI 提供商调用日志最大保留行数（超额删最旧；0=不限行数）", 1309002301440000000L, "50000" },
                    { "Audit_AiCallRetentionDays", "Audit", 1309002301440000000L, "AI 调用日志保留天数（超期清理；0=不按天清理）", 1309002301440000000L, "90" },
                    { "Backup_Enabled", "Backup", 1309005840384000000L, "自动备份开关（每日 04:00 在线快照数据库 + 密钥环到 backups 目录）", 1309005840384000000L, "true" },
                    { "Backup_RetainCount", "Backup", 1309005840384000000L, "备份保留份数（超出删最旧；范围 [1, 365]）", 1309005840384000000L, "7" },
                    { "File.CleanEmptyDir", "General", 1308968681472000000L, "归档完成后清理源端空目录", 1308968681472000000L, "false" },
                    { "File.Operation", "General", 1308968681472000000L, "文件操作方式（Move / Copy / Link）", 1308968681472000000L, "Move" },
                    { "Log.Level", "Log", 1308968681472000000L, "日志级别（Trace / Debug / Information / Warning / Error）", 1308968681472000000L, "Information" },
                    { "Parse.AiConfidenceThreshold", "Parse", 1308968681472000000L, "AI 兜底解析置信度阈值", 1308968681472000000L, "0.7" },
                    { "Parse.ConfidenceThreshold", "Parse", 1308968681472000000L, "解析置信度阈值（低于则进入人工确认）", 1308968681472000000L, "0.6" },
                    { "Proxy_BypassList", "Proxy", 1308989620224000000L, "额外 bypass 规则，逗号或换行分隔（如 *.cn,corp.local）", 1308989620224000000L, null },
                    { "Proxy_Enabled", "Proxy", 1308989620224000000L, "代理总开关（true / false）", 1308989620224000000L, "false" },
                    { "Proxy_HttpUrl", "Proxy", 1308989620224000000L, "HTTP 代理地址（如 http://127.0.0.1:7890）", 1308989620224000000L, null },
                    { "Proxy_UseForTmdb", "Proxy", 1308989620224000000L, "TMDB 客户端是否走代理（true / false）", 1308989620224000000L, "true" },
                    { "Proxy_UseForUpdateCheck", "Proxy", 1308989620224000000L, "GitHub 升级检查是否走代理（true / false）", 1308989620224000000L, "true" },
                    { "Scan.IntervalHours", "General", 1308968681472000000L, "全量扫描周期（小时）", 1308968681472000000L, "12" },
                    { "Stability.SecondsBeforeReady", "General", 1308968681472000000L, "文件稳定判定时长（秒，写入静默达此值后视为就绪）", 1308968681472000000L, "5" },
                    { "System_AlertSuppressMinutes", "Alert", 1309006725120000000L, "同类告警抑制窗口（分钟，窗口内同一告警只发一次，防风暴）", 1309006725120000000L, "60" },
                    { "Update_CheckIntervalHours", "Update", 1308989030400000000L, "自动检查周期（小时，[1, 720]）", 1308989030400000000L, "24" },
                    { "Update_Enabled", "Update", 1308989030400000000L, "自动检查启用开关（true / false）", 1308989030400000000L, "true" },
                    { "Update_GitHubOwner", "Update", 1308988145664000000L, "GitHub 仓库所有者（升级检查数据源）", 1308988145664000000L, "mdjs147" },
                    { "Update_GitHubPat", "Update", 1308988145664000000L, "GitHub PAT（加密存储；空 = 匿名 60 req/h，配置 = 5000 req/h）", 1308988145664000000L, null },
                    { "Update_GitHubRepo", "Update", 1308988145664000000L, "GitHub 仓库名（升级检查数据源）", 1308988145664000000L, "PersonalMediaManager" },
                    { "Update_LastCheckJson", "Update", 1308988145664000000L, "上次升级检查结果 JSON 快照（含 etag/error/latest）", 1308988145664000000L, null },
                    { "Update_SkippedVersion", "Update", 1308988145664000000L, "用户跳过的版本号（命中后不再气泡提示）", 1308988145664000000L, null },
                    { "Web.Port", "General", 1308968681472000000L, "Web 服务端口", 1308968681472000000L, "7288" },
                    { "Webhook_Enabled", "Webhook", 1309000531968000000L, "Webhook 总开关（false=关闭时归档不产生投递记录）", 1309000531968000000L, "false" }
                });

            migrationBuilder.InsertData(
                table: "Tmdb_Setting",
                columns: new[] { "Id", "ApiKeyEncrypted", "CandidateThreshold", "CreatedAt", "FallbackLanguage", "Language", "MetadataCacheHours", "RateLimitPerSecond", "ScoreWeightLanguage", "ScoreWeightPopularity", "ScoreWeightTitle", "ScoreWeightYear", "SearchCacheMinutes", "UpdatedAt" },
                values: new object[] { 1L, null, 3, 1308968681472000000L, "en-US", "zh-CN", 24, 40, 0.10000000000000001, 0.10000000000000001, 0.5, 0.29999999999999999, 60, 1308968681472000000L });

            migrationBuilder.InsertData(
                table: "Watch_IgnoreRule",
                columns: new[] { "Id", "CreatedAt", "Description", "Enabled", "Pattern", "Type", "UpdatedAt" },
                values: new object[,]
                {
                    { 1L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".part", "Extension", 1308968681472000000L },
                    { 2L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".tmp", "Extension", 1308968681472000000L },
                    { 3L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".crdownload", "Extension", 1308968681472000000L },
                    { 4L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".download", "Extension", 1308968681472000000L },
                    { 5L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".!qb", "Extension", 1308968681472000000L },
                    { 6L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".downloading", "Extension", 1308968681472000000L },
                    { 7L, 1308968681472000000L, "默认忽略下载中临时文件", true, ".complete", "Extension", 1308968681472000000L },
                    { 8L, 1308968681472000000L, "BT 种子描述文件", true, ".torrent", "Extension", 1308968681472000000L },
                    { 9L, 1308968681472000000L, "迅雷下载临时文件", true, ".xltd", "Extension", 1308968681472000000L },
                    { 10L, 1308968681472000000L, "迅雷下载临时文件", true, ".td", "Extension", 1308968681472000000L },
                    { 11L, 1308968681472000000L, "uTorrent 未完成文件", true, ".!ut", "Extension", 1308968681472000000L },
                    { 12L, 1308968681472000000L, "BitComet 未完成文件", true, ".bc!", "Extension", 1308968681472000000L },
                    { 13L, 1308968681472000000L, "aria2 下载控制文件", true, ".aria2", "Extension", 1308968681472000000L },
                    { 14L, 1308968681472000000L, "下载中临时文件（Edge / IDM 等）", true, ".partial", "Extension", 1308968681472000000L },
                    { 15L, 1308968681472000000L, "Opera 下载中临时文件", true, ".opdownload", "Extension", 1308968681472000000L },
                    { 16L, 1308968681472000000L, "Free Download Manager 下载临时文件", true, ".fdmdownload", "Extension", 1308968681472000000L }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_AiCall_ChainId",
                table: "Audit_AiCall",
                column: "ChainId");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_AiCall_MediaItemId",
                table: "Audit_AiCall",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_AiCall_ProviderId_Success_Timestamp",
                table: "Audit_AiCall",
                columns: new[] { "ProviderId", "Success", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_AiCall_ProviderId_Timestamp",
                table: "Audit_AiCall",
                columns: new[] { "ProviderId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Operation_Action_Timestamp",
                table: "Audit_Operation",
                columns: new[] { "Action", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Operation_UserId_Timestamp",
                table: "Audit_Operation",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_ScheduledTaskRun_JobKey_StartedAt",
                table: "Audit_ScheduledTaskRun",
                columns: new[] { "JobKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_ScheduledTaskRun_StartedAt",
                table: "Audit_ScheduledTaskRun",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Category_Definition_MediaType",
                table: "Category_Definition",
                column: "MediaType");

            migrationBuilder.CreateIndex(
                name: "UQ_Category_Definition_Name",
                table: "Category_Definition",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_MatchRule_CategoryId",
                table: "Category_MatchRule",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Category_MatchRule_Enabled_Priority",
                table: "Category_MatchRule",
                columns: new[] { "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "UQ_Media_Episode_WorkId_Season_Episode",
                table: "Media_Episode",
                columns: new[] { "WorkId", "SeasonNumber", "EpisodeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_Item_CategoryId",
                table: "Media_Item",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_Item_FileHash",
                table: "Media_Item",
                column: "FileHash");

            migrationBuilder.CreateIndex(
                name: "IX_Media_Item_Status_CreatedAt",
                table: "Media_Item",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Media_Item_TmdbId",
                table: "Media_Item",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "UQ_Media_Item_SourcePath",
                table: "Media_Item",
                column: "SourcePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Media_Season_WorkId_SeasonNumber",
                table: "Media_Season",
                columns: new[] { "WorkId", "SeasonNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_Work_CategoryId",
                table: "Media_Work",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_Work_VoteAverage",
                table: "Media_Work",
                column: "VoteAverage");

            migrationBuilder.CreateIndex(
                name: "IX_Media_Work_Year",
                table: "Media_Work",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "UQ_Media_Work_TmdbId_MediaType",
                table: "Media_Work",
                columns: new[] { "TmdbId", "MediaType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkCompany_CompanyId",
                table: "Media_WorkCompany",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkCredit_PersonId",
                table: "Media_WorkCredit",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkCredit_WorkId",
                table: "Media_WorkCredit",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkGenre_GenreId",
                table: "Media_WorkGenre",
                column: "GenreId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkKeyword_KeywordId",
                table: "Media_WorkKeyword",
                column: "KeywordId");

            migrationBuilder.CreateIndex(
                name: "IX_Media_WorkNetwork_NetworkId",
                table: "Media_WorkNetwork",
                column: "NetworkId");

            migrationBuilder.CreateIndex(
                name: "IX_Parse_AiProvider_DisabledUntil",
                table: "Parse_AiProvider",
                column: "DisabledUntil");

            migrationBuilder.CreateIndex(
                name: "IX_Parse_AiProvider_Enabled_Priority",
                table: "Parse_AiProvider",
                columns: new[] { "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_Parse_AiProvider_IsPrimary",
                table: "Parse_AiProvider",
                column: "IsPrimary");

            migrationBuilder.CreateIndex(
                name: "UQ_Parse_AiProvider_Name",
                table: "Parse_AiProvider",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parse_Rule_Enabled_Priority",
                table: "Parse_Rule",
                columns: new[] { "Enabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "UQ_Parse_Rule_Name",
                table: "Parse_Rule",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parse_TestCase_LastMatchedRuleId",
                table: "Parse_TestCase",
                column: "LastMatchedRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Parse_TestCase_Status_CreatedAt",
                table: "Parse_TestCase",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UQ_Parse_TestCase_SamplePath",
                table: "Parse_TestCase",
                column: "SamplePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Process_Step_MediaItemId_StartedAt",
                table: "Process_Step",
                columns: new[] { "MediaItemId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_System_MediaExtension_Enabled",
                table: "System_MediaExtension",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "UQ_System_MediaExtension_Extension",
                table: "System_MediaExtension",
                column: "Extension",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_System_Setting_Category",
                table: "System_Setting",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Tmdb_MetadataCache_CachedAt",
                table: "Tmdb_MetadataCache",
                column: "CachedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tmdb_SearchCache_CachedAt",
                table: "Tmdb_SearchCache",
                column: "CachedAt");

            migrationBuilder.CreateIndex(
                name: "IX_User_Account_Role",
                table: "User_Account",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "UQ_User_Account_Username",
                table: "User_Account",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watch_Folder_Enabled_IsNetworkShare",
                table: "Watch_Folder",
                columns: new[] { "Enabled", "IsNetworkShare" });

            migrationBuilder.CreateIndex(
                name: "UQ_Watch_Folder_Path",
                table: "Watch_Folder",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watch_IgnoreRule_Type_Enabled",
                table: "Watch_IgnoreRule",
                columns: new[] { "Type", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "UQ_Watch_IgnoreRule_Type_Pattern",
                table: "Watch_IgnoreRule",
                columns: new[] { "Type", "Pattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Webhook_Delivery_Event",
                table: "Webhook_Delivery",
                column: "Event");

            migrationBuilder.CreateIndex(
                name: "IX_Webhook_Delivery_RequestId",
                table: "Webhook_Delivery",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Webhook_Delivery_Status_NextRetryAt",
                table: "Webhook_Delivery",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Webhook_Delivery_SubscriptionId_Status_LastTriedAt",
                table: "Webhook_Delivery",
                columns: new[] { "SubscriptionId", "Status", "LastTriedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Webhook_Subscription_Enabled",
                table: "Webhook_Subscription",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "UQ_Webhook_Subscription_Name",
                table: "Webhook_Subscription",
                column: "Name",
                unique: true);

            // 默认解析规则种子：23 条按 Name 幂等插入（成员定义见类尾，来源说明见 ParseRuleSeeds 注释）
            SeedParseRuleDefaults(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit_AiCall");

            migrationBuilder.DropTable(
                name: "Audit_Operation");

            migrationBuilder.DropTable(
                name: "Audit_ScheduledTaskRun");

            migrationBuilder.DropTable(
                name: "Category_MatchRule");

            migrationBuilder.DropTable(
                name: "Media_Episode");

            migrationBuilder.DropTable(
                name: "Media_Season");

            migrationBuilder.DropTable(
                name: "Media_WorkCompany");

            migrationBuilder.DropTable(
                name: "Media_WorkCredit");

            migrationBuilder.DropTable(
                name: "Media_WorkGenre");

            migrationBuilder.DropTable(
                name: "Media_WorkKeyword");

            migrationBuilder.DropTable(
                name: "Media_WorkNetwork");

            migrationBuilder.DropTable(
                name: "Parse_Rule");

            migrationBuilder.DropTable(
                name: "Parse_TestCase");

            migrationBuilder.DropTable(
                name: "Process_Step");

            migrationBuilder.DropTable(
                name: "System_MediaExtension");

            migrationBuilder.DropTable(
                name: "System_Setting");

            migrationBuilder.DropTable(
                name: "Tmdb_MetadataCache");

            migrationBuilder.DropTable(
                name: "Tmdb_SearchCache");

            migrationBuilder.DropTable(
                name: "Tmdb_Setting");

            migrationBuilder.DropTable(
                name: "Watch_Folder");

            migrationBuilder.DropTable(
                name: "Watch_IgnoreRule");

            migrationBuilder.DropTable(
                name: "Webhook_Delivery");

            migrationBuilder.DropTable(
                name: "Parse_AiProvider");

            migrationBuilder.DropTable(
                name: "User_Account");

            migrationBuilder.DropTable(
                name: "Media_Company");

            migrationBuilder.DropTable(
                name: "Media_Person");

            migrationBuilder.DropTable(
                name: "Media_Genre");

            migrationBuilder.DropTable(
                name: "Media_Keyword");

            migrationBuilder.DropTable(
                name: "Media_Network");

            migrationBuilder.DropTable(
                name: "Media_Work");

            migrationBuilder.DropTable(
                name: "Media_Item");

            migrationBuilder.DropTable(
                name: "Webhook_Subscription");

            migrationBuilder.DropTable(
                name: "Category_Definition");
        }

        // ============================================================================
        // 2026-06-11 迁移合并：历史 29 个 migration 收敛为本单一 Init。
        // 以下默认解析规则种子原样移植自三个历史迁移的手写 SQL（非 HasData，模型快照不含）：
        //   第一批 8 条 ← 20260527160000_BackfillParseRuleDefaults
        //   第二批 7 条 ← 20260527170000_BackfillParseRuleDefaultsV2
        //   第三批 8 条 ← 20260528120000_BackfillParseRuleDefaultsV3
        // 行为约定：迁移插完 23 条后 DataSeeder 见 Parse_Rule 非空即跳过其种子分支，
        // 个别旧 Pattern 仍由运行时 FixLegacyParseRulesAsync 按需修正，与历史链行为一致。
        // ============================================================================

        /// <summary>第一/二批种子时间戳（DateTimeOffsetToBinaryConverter 编码的「2026-05-27」近似值）</summary>
        private const long TsBatch12 = 1308989620224000000L;

        /// <summary>第三批种子时间戳（同精度「2026-05-28」近似值）</summary>
        private const long TsBatch3 = 1308996940800000000L;

        /// <summary>(Name, Pattern, DefaultType, ForceType, Priority, ConfidenceBonus, Description, Ts)，顺序 = 历史插入顺序</summary>
        private static readonly (string Name, string Pattern, string DefaultType, bool ForceType, int Priority, double Bonus, string Description, long Ts)[] ParseRuleSeeds =
        {
            // —— 第一批（原 BackfillParseRuleDefaults，ForceType 全为否）——
            ("国产剧第N季第N集",
                @"^(?<title>.+?)[\s\._\-]*第(?<season>\d{1,2})季[\s\._\-]*第(?<episode>\d{1,4})[集话話]",
                "tv", false, 25, 0.05,
                "国产/华语剧「第N季第N集」组合命名（如「琅琊榜.第1季.第03集」）", TsBatch12),

            ("Plex Jellyfin 标准命名",
                @"^(?<title>.+?)\s+-\s+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})\s+-\s+",
                "tv", false, 30, 0.05,
                "Plex / Jellyfin 推荐格式「Show - SxxExx - Title」，标题与季集分隔最干净", TsBatch12),

            ("括号年份带季集",
                @"^(?<title>.+?)\s*\((?<year>\d{4})\)[\s\.\-_]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})",
                "tv", false, 35, 0.05,
                "「Show Name (2020) S01E01」组合命名，一次抓 title/year/season/episode", TsBatch12),

            ("方括号包裹剧集",
                @"^(?:\[[^\]]{1,40}\]\s*)*\[(?<title>[^\[\]]{1,40})\]\s*\[(?<episode>\d{1,4})(?:v\d)?\]",
                "tv", false, 45, 0.05,
                "纯方括号链命名「[字幕组][剧名][01][1080p]」，国漫/番剧常见", TsBatch12),

            ("动漫字幕组单集",
                @"^(?:\[[^\]]{1,40}\]\s*)+(?<title>[^\[\]]+?)\s*-\s*(?<episode>\d{1,4})(?:v\d)?\s*(?:\[|\.|$)",
                "tv", false, 50, 0.05,
                "匹配方括号字幕组前缀 + 「标题 - 集号」的动漫单集命名", TsBatch12),

            ("OVA SP 特别篇",
                @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+(?:OVA|SP|NCED|NCOP|番外|特典|映画|剧场版)[\s\._\-]?(?<episode>\d{1,3})(?![\d])",
                "tv", false, 55, 0.0,
                "OVA / SP / 番外 / 特典 等动漫特别篇标记，集号 1-3 位", TsBatch12),

            ("季集 NxNN 格式",
                @"^(?<title>.+?)[\s\._\-]+(?<season>\d{1,2})x(?<episode>\d{1,3})(?:[\s\._\-]|$)",
                "tv", false, 60, 0.0,
                "匹配「季x集」（如 1x02）写法的剧集命名", TsBatch12),

            ("括号年份电影",
                @"^(?<title>.+?)\s*\((?<year>\d{4})\)",
                null, false, 70, 0.0,
                "「电影名 (2020) ...」括号年份命名，标题剥得比内置规则干净", TsBatch12),

            // —— 第二批（原 BackfillParseRuleDefaultsV2）——
            ("综艺第N季 + 日期作集",
                @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})季[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
                "tv", false, 22, 0.05,
                "综艺命名「节目名 第N季 YYYYMMDD」：season 抓季号，year 抓年份，episode 抓 MMDD（按时间顺序排序）", TsBatch12),

            ("综艺日期作集",
                @"^(?<title>.+?)[\s\._\-]+(?<year>20\d{2})(?<episode>\d{4})(?:[\s\._\-]|$)",
                "tv", false, 28, 0.05,
                "综艺 / 真人秀「节目名 YYYYMMDD」：year 抓年份，episode 抓 MMDD（按时间顺序排序）", TsBatch12),

            ("AKA 多语言标题",
                @"^(?<title>.+?)[\s\._\-]+(?:AKA|aka|a\.k\.a\.|也叫)[\s\._\-]+.+?[\s\._\-]+[Ss](?<season>\d{1,2})[Ee](?<episode>\d{1,4})",
                "tv", false, 32, 0.05,
                "AKA 多语言标题命名「主标题 AKA 别名 SxxEyy」，取 AKA 前的主标题", TsBatch12),

            ("Anime 英文季号 (Nth Season)",
                @"^(?<title>.+?)[\s\._\-]+(?<season>\d{1,2})(?:st|nd|rd|th)[\s\._\-]?Season[\s\._\-]+(?:E|EP|Episode)?[\s\._\-]?(?<episode>\d{1,4})",
                "tv", false, 48, 0.05,
                "番剧英文季号「Show 2nd Season E01」，捕获 season 数字（1st/2nd/3rd/Nth）+ episode", TsBatch12),

            ("全N集 整季合集",
                @"^(?<title>.+?)[\s\._\-]+全(?<episode>\d{1,4})集",
                "tv", true, 65, 0.0,
                "整季合集「全N集」命名（如扫毒.全30集），识别为 tv；episode 抓总集数仅作展示", TsBatch12),

            ("Episode N 完整英文",
                @"^(?<title>.+?)[\s\._\-]+Episode[\s\._\-]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
                "tv", false, 75, 0.0,
                "「Show Episode 12」完整英文集号命名，补内置 EP/E 简写规则未覆盖的全词形式", TsBatch12),

            ("中文章节集号「第N章 / 第N回」",
                @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[章回段]",
                "tv", false, 80, 0.0,
                "中文「第N章 / 第N回 / 第N段」章节集号，覆盖漫画化番剧 / 网络剧 / 评书命名", TsBatch12),

            // —— 第三批（原 BackfillParseRuleDefaultsV3）——
            ("中文「第N部 第N集」",
                @"^(?<title>.+?)[\s\._\-]+第(?<season>\d{1,2})部[\s\._\-]+第(?<episode>\d{1,4})[集话話]",
                "tv", false, 26, 0.05,
                "国剧 / 系列剧「第N部 第N集」组合命名（如「我的天才女友 第2部 第12集」），「部」作为分季单位", TsBatch3),

            ("方括号双段季集 [Sxx][Eyy]",
                @"\[[Ss](?<season>\d{1,2})\]\s*\[[Ee][Pp]?(?<episode>\d{1,4})\]",
                "tv", false, 27, 0.05,
                "字幕组双方括号格式「[番名][S01][E12]」，季集各自独立方括号且带 S/E 前缀", TsBatch3),

            ("综艺「第N期」",
                @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})期",
                "tv", false, 29, 0.05,
                "韩综 / 日综 / 真人秀「第N期」集号命名（如 Knowing Bros 第300期），「期」作为集号单位", TsBatch3),

            ("番剧分卷 Part / Cour + 集号",
                @"^(?<title>.+?)[\s\._\-]+(?:(?:Part|Cour)[\s\._\-]?\d{1,2}|\d(?:st|nd|rd|th)[\s\._\-]?Cour)[\s\._\-]+(?:E|EP|Episode)[\s\._\-]?(?<episode>\d{1,4})",
                "tv", false, 49, 0.05,
                "番剧分卷标记「Show Part 2 E03」「Show 2nd Cour E05」，title + episode（season 留空交 TMDB 反查）", TsBatch3),

            ("动漫 Vol / Volume 卷集号",
                @"^(?:\[[^\]]{1,40}\]\s*)*(?<title>[^\[\]]+?)[\s\._\-]+Vol(?:ume)?\.?[\s\._\-]?(?<episode>\d{1,3})(?:[\s\._\-\[]|$)",
                "tv", false, 52, 0.0,
                "动漫 BD/BDBox 卷标记「Vol.01」「Volume 3」，集号 1-3 位（一卷代表数集）", TsBatch3),

            ("绝对集号「#N / No.N」",
                @"^(?<title>.+?)[\s\._\-]+(?:#|No\.)[\s\._]?(?<episode>\d{1,4})(?:[\s\._\-]|$)",
                "tv", false, 82, 0.0,
                "长篇番剧绝对集号「Show #N」「Show No.N」（如 One Piece #1000），无季号 + 大集号", TsBatch3),

            ("无季号「第N集」中文兜底",
                @"^(?<title>.+?)[\s\._\-]+第(?<episode>\d{1,4})[集话話](?:[\s\._\-]|$)",
                "tv", false, 83, 0.0,
                "无季号场景下纯中文「第N集 / 第N话 / 第N話」单集兜底，title 抓得比内置规则干净", TsBatch3),

            ("综艺 YYMMDD 短日期作集",
                @"^(?<title>.+?)[\s\._\-]+(?<episode>[12]\d{5})(?:[\s\._\-]|$)",
                "tv", false, 85, 0.0,
                "韩综 / 日综短日期「节目名 YYMMDD」，6 位日期（10-29 年范围）作 episode 排序", TsBatch3),
        };

        /// <summary>把 23 条默认解析规则按 Name 幂等插入 Parse_Rule</summary>
        private static void SeedParseRuleDefaults(MigrationBuilder migrationBuilder)
        {
            foreach (var rule in ParseRuleSeeds)
            {
                string defaultTypeLiteral = rule.DefaultType is null ? "NULL" : $"'{rule.DefaultType}'";
                string bonusLiteral = rule.Bonus.ToString("G17", CultureInfo.InvariantCulture);
                int forceTypeInt = rule.ForceType ? 1 : 0;

                migrationBuilder.Sql($@"
INSERT INTO ""Parse_Rule"" (
    ""Name"", ""Scope"", ""Pattern"", ""DefaultType"", ""ForceType"",
    ""Priority"", ""ConfidenceBonus"", ""Enabled"", ""Description"",
    ""CreatedAt"", ""UpdatedAt"", ""RowVersion"")
SELECT
    '{Escape(rule.Name)}', 'FileName', '{Escape(rule.Pattern)}', {defaultTypeLiteral}, {forceTypeInt},
    {rule.Priority}, {bonusLiteral}, 1, '{Escape(rule.Description)}',
    {rule.Ts}, {rule.Ts}, 0
WHERE NOT EXISTS (SELECT 1 FROM ""Parse_Rule"" WHERE ""Name"" = '{Escape(rule.Name)}');");
            }
        }

        /// <summary>SQLite 单引号转义</summary>
        private static string Escape(string s) => s.Replace("'", "''");
    }
}
