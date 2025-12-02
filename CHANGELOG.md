# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [v1.0.4]

### Added

- **EventProperties persistence**: EventProperties, MapName, and Flag are now properly saved to and loaded from database
- **Extended primitive support**: EventProperties now supports all primitive types (bool, string, byte, sbyte, short, ushort, int, uint, long, ulong, float, double, decimal)
- **New database columns**: Added `event_properties`, `map_name`, and `flag` columns via M002 migration

### Fixed

- Fixed EventProperties not being persisted to database (missions with filters like weapon/headshot now work correctly after server restart)
- Fixed MapName restriction not being loaded from database
- Fixed Flag permission not being loaded from database

## [v1.0.3]

### Fixed

- Fixed migration version ID conflict with FluentMigrator

## [v1.0.2]

### Added

- **Multi-database support**: Now supports MySQL/MariaDB, PostgreSQL, and SQLite
- **Database migrations**: Automatic schema management with FluentMigrator
- **ORM integration**: Dapper + Dommel for type-safe database operations

### Changed

- Refactored database layer to use Dommel ORM instead of raw SQL queries
- Improved database compatibility across different database engines
- Optimized publish output by excluding unused language resources and database providers

### Fixed

- Fixed SQL syntax compatibility issues with different database engines
