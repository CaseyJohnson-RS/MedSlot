using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MedSlot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    partition_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false),
                    trace_parent = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    occurred_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_event_id",
                table: "outbox_messages",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_published_at",
                table: "outbox_messages",
                column: "published_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "id",
                filter: "published_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");
        }
    }
}
