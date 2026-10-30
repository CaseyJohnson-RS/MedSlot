using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace MedSlot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Waitlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_from = table.Column<LocalDate>(type: "date", nullable: false),
                    date_to = table.Column<LocalDate>(type: "date", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    queued_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expired_offers_count = table.Column<int>(type: "integer", nullable: false),
                    current_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointments_hold_expires_at",
                table: "appointments",
                column: "hold_expires_at",
                filter: "status = 'Held'");

            migrationBuilder.CreateIndex(
                name: "ix_appointments_waitlist_entry_id",
                table: "appointments",
                column: "waitlist_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_doctor_id_status_queued_at",
                table: "waitlist_entries",
                columns: new[] { "doctor_id", "status", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_service_id",
                table: "waitlist_entries",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ux_waitlist_entries_open",
                table: "waitlist_entries",
                columns: new[] { "patient_id", "doctor_id", "service_id" },
                unique: true,
                filter: "status IN ('Active', 'Offered')");

            migrationBuilder.AddForeignKey(
                name: "fk_appointments_waitlist_entries_waitlist_entry_id",
                table: "appointments",
                column: "waitlist_entry_id",
                principalTable: "waitlist_entries",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_appointments_waitlist_entries_waitlist_entry_id",
                table: "appointments");

            migrationBuilder.DropTable(
                name: "waitlist_entries");

            migrationBuilder.DropIndex(
                name: "ix_appointments_hold_expires_at",
                table: "appointments");

            migrationBuilder.DropIndex(
                name: "ix_appointments_waitlist_entry_id",
                table: "appointments");
        }
    }
}
