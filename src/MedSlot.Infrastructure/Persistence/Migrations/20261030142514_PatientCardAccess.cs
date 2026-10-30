using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace MedSlot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PatientCardAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_card_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accessor_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    accessor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    doctor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    accessed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patient_card_access", x => x.id);
                    table.ForeignKey(
                        name: "fk_patient_card_access_doctors_doctor_id",
                        column: x => x.doctor_id,
                        principalTable: "doctors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_patient_card_access_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_patient_card_access_doctor_id",
                table: "patient_card_access",
                column: "doctor_id");

            migrationBuilder.CreateIndex(
                name: "ix_patient_card_access_patient_id_accessed_at",
                table: "patient_card_access",
                columns: new[] { "patient_id", "accessed_at" });

            // Журнал доступа к персональным данным только пополняется (ADR 0012): следы чтения нельзя
            // исправить или стереть. Триггер действует и на владельца таблицы, в отличие от REVOKE.
            migrationBuilder.Sql("""
                CREATE FUNCTION patient_card_access_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'patient_card_access только пополняется: % запрещён', TG_OP;
                END
                $$;

                CREATE TRIGGER patient_card_access_no_update_delete
                    BEFORE UPDATE OR DELETE ON patient_card_access
                    FOR EACH ROW EXECUTE FUNCTION patient_card_access_append_only();

                CREATE TRIGGER patient_card_access_no_truncate
                    BEFORE TRUNCATE ON patient_card_access
                    FOR EACH STATEMENT EXECUTE FUNCTION patient_card_access_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION patient_card_access_append_only() CASCADE;");

            migrationBuilder.DropTable(
                name: "patient_card_access");
        }
    }
}
