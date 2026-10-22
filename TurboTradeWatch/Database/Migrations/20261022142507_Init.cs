using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboTradeWatch.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_cursor",
                    columns: table => new
                    {
                        name = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        last_id = table.Column<long>(type: "bigint", nullable: false),
                        missed = table.Column<long>(type: "bigint", nullable: false),
                        last_gap = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        pruned_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_cursor", x => x.name);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_flags",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        rule = table
                            .Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        dedupe_key = table
                            .Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        subject_ref = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        player_id = table.Column<int>(type: "int", nullable: false),
                        other_player_ids = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        value = table.Column<long>(type: "bigint", nullable: false),
                        summary = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        evidence = table
                            .Column<string>(type: "text", maxLength: 512, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        operations = table
                            .Column<string>(type: "text", maxLength: 512, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        status = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        note = table
                            .Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        handled_by_id = table.Column<int>(type: "int", nullable: true),
                        handled_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_seen_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        notified_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_flags", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_handovers",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        chain_id = table.Column<long>(type: "bigint", nullable: false),
                        asset_id = table.Column<long>(type: "bigint", nullable: false),
                        definition_id = table.Column<int>(type: "int", nullable: false),
                        operation_id = table
                            .Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        from_player_id = table.Column<int>(type: "int", nullable: false),
                        to_player_id = table.Column<int>(type: "int", nullable: false),
                        occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_handovers", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_movements",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        outbox_id = table.Column<long>(type: "bigint", nullable: false),
                        operation_id = table
                            .Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        seq = table.Column<short>(type: "smallint", nullable: false),
                        occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        cause = table
                            .Column<string>(type: "varchar(48)", maxLength: 48, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        cause_ref = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        actor_id = table.Column<int>(type: "int", nullable: false),
                        asset_kind = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        asset_id = table.Column<long>(type: "bigint", nullable: false),
                        definition_id = table.Column<int>(type: "int", nullable: false),
                        currency = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        amount = table.Column<long>(type: "bigint", nullable: false),
                        from_player_id = table.Column<int>(type: "int", nullable: false),
                        from_kind = table
                            .Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        from_place_id = table.Column<long>(type: "bigint", nullable: false),
                        to_player_id = table.Column<int>(type: "int", nullable: false),
                        to_kind = table
                            .Column<string>(type: "varchar(24)", maxLength: 24, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        to_place_id = table.Column<long>(type: "bigint", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_movements", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_transfers",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        operation_id = table
                            .Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        kind = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        from_player_id = table.Column<int>(type: "int", nullable: false),
                        to_player_id = table.Column<int>(type: "int", nullable: false),
                        occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        value = table.Column<long>(type: "bigint", nullable: false),
                        credits = table.Column<long>(type: "bigint", nullable: false),
                        items = table.Column<int>(type: "int", nullable: false),
                        unvalued = table.Column<int>(type: "int", nullable: false),
                        ltds = table.Column<int>(type: "int", nullable: false),
                        summary = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_transfers", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "tradewatch_values",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        definition_id = table.Column<int>(type: "int", nullable: false),
                        credits = table.Column<int>(type: "int", nullable: false),
                        is_ltd = table.Column<bool>(type: "tinyint(1)", nullable: false),
                        source = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        note = table
                            .Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        updated_by_id = table.Column<int>(type: "int", nullable: false),
                        updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_tradewatch_values", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_flags_dedupe_key",
                table: "tradewatch_flags",
                column: "dedupe_key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_flags_player_id",
                table: "tradewatch_flags",
                column: "player_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_flags_rule_subject_ref_last_seen_at",
                table: "tradewatch_flags",
                columns: new[] { "rule", "subject_ref", "last_seen_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_flags_status_id",
                table: "tradewatch_flags",
                columns: new[] { "status", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_handovers_asset_id",
                table: "tradewatch_handovers",
                column: "asset_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_handovers_chain_id_occurred_at",
                table: "tradewatch_handovers",
                columns: new[] { "chain_id", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_handovers_operation_id_asset_id",
                table: "tradewatch_handovers",
                columns: new[] { "operation_id", "asset_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_movements_asset_id",
                table: "tradewatch_movements",
                column: "asset_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_movements_occurred_at",
                table: "tradewatch_movements",
                column: "occurred_at"
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_movements_operation_id_seq",
                table: "tradewatch_movements",
                columns: new[] { "operation_id", "seq" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_transfers_from_player_id_occurred_at",
                table: "tradewatch_transfers",
                columns: new[] { "from_player_id", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_transfers_operation_id_from_player_id_to_player_id",
                table: "tradewatch_transfers",
                columns: new[] { "operation_id", "from_player_id", "to_player_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_transfers_to_player_id_occurred_at",
                table: "tradewatch_transfers",
                columns: new[] { "to_player_id", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_tradewatch_values_definition_id",
                table: "tradewatch_values",
                column: "definition_id",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "tradewatch_cursor");

            migrationBuilder.DropTable(name: "tradewatch_flags");

            migrationBuilder.DropTable(name: "tradewatch_handovers");

            migrationBuilder.DropTable(name: "tradewatch_movements");

            migrationBuilder.DropTable(name: "tradewatch_transfers");

            migrationBuilder.DropTable(name: "tradewatch_values");
        }
    }
}
