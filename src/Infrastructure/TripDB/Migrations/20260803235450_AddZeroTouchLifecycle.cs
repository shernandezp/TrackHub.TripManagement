using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace TrackHub.TripManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddZeroTouchLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "armedat",
                schema: "trip",
                table: "trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "originarrivedat",
                schema: "trip",
                table: "trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "origindepartedat",
                schema: "trip",
                table: "trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origingeofenceid",
                schema: "trip",
                table: "trips",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Polygon>(
                name: "origingeom",
                schema: "trip",
                table: "trips",
                type: "geometry (Polygon, 4326)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "originoutsidesinceat",
                schema: "trip",
                table: "trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "originradiusmeters",
                schema: "trip",
                table: "trips",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            // NO DDL for the `xmin` concurrency token, and that is deliberate. It is PostgreSQL's
            // own system column, present on every table since it was created; EF scaffolded an
            // AddColumn for it only because the model now maps it, and running that fails with
            // "column name xmin conflicts with a system column name". The mapping IS the change.

            migrationBuilder.AddColumn<string>(
                name: "activity",
                schema: "trip",
                table: "trip_stops",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Unload");

            migrationBuilder.CreateIndex(
                name: "ix_trips_origingeom_gist",
                schema: "trip",
                table: "trips",
                column: "origingeom")
                .Annotation("Npgsql:IndexMethod", "gist");

            // The unique index below asserts "one physical unit runs one trip at a time". Nothing
            // enforced that before, so existing data may already violate it — and a bare CREATE
            // UNIQUE INDEX would fail with Postgres's own message naming two row ids and nothing
            // else. Fail LOUDLY instead, naming the vehicles, so the operator can resolve the
            // duplicates before applying (spec 11a §14).
            //
            // Paused is in scope alongside InProgress: a paused trip has not released its vehicle,
            // and treating it as idle let the next trip in the queue auto-start on the same unit.
            migrationBuilder.Sql("""
                DO $$
                DECLARE offenders text;
                BEGIN
                    SELECT string_agg(transporterid::text, ', ')
                    INTO offenders
                    FROM (
                        SELECT transporterid
                        FROM trip.trips
                        WHERE status IN ('InProgress', 'Paused')
                        GROUP BY transporterid
                        HAVING count(*) > 1
                    ) duplicates;

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION
                            'AddZeroTouchLifecycle cannot apply: these transporters have more than one open (InProgress or Paused) trip: %. Complete, cancel or abort the extra trips, then re-run.',
                            offenders;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_trips_transporterid_inprogress",
                schema: "trip",
                table: "trips",
                column: "transporterid",
                unique: true,
                filter: "status IN ('InProgress', 'Paused')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_trips_origingeom_gist",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropIndex(
                name: "ux_trips_transporterid_inprogress",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "armedat",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "originarrivedat",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "origindepartedat",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "origingeofenceid",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "origingeom",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "originoutsidesinceat",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "originradiusmeters",
                schema: "trip",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "activity",
                schema: "trip",
                table: "trip_stops");
        }
    }
}
