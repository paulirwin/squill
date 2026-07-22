# Sakila sample database (MariaDB / MySQL)

A Squill port of the classic [Sakila](https://github.com/jOOQ/sakila) DVD-rental
schema — the MySQL counterpart to SQL Server's AdventureWorks — expressed as
declarative `.sql` files, one object per file. It is a deliberately ambitious,
production-style schema meant to exercise the MariaDB provider well beyond the
small [MariaDbSampleDatabase](../MariaDbSampleDatabase).

What it covers: 16 tables with `AUTO_INCREMENT` keys and foreign keys (including
the circular `staff`↔`store` pair), `ENUM` / `SET` / `YEAR` columns,
`ON UPDATE CURRENT_TIMESTAMP`, a `FULLTEXT` index, seven views, stored
procedures, stored functions, and triggers.

## Status

This sample is **aspirational**: it intentionally includes features Squill's
MariaDB provider does not fully support yet, so it doubles as a living gap list.
Today:

- **Builds** (parses to a DACPAC) for the supported subset — all tables, views,
  procedures, and triggers.
- **Does not yet deploy** end-to-end, because of two provider gaps:
  1. **`ENUM` / `SET` script generation** — these columns parse, but the
     generated DDL drops the value list (emits `enum NULL` / `set NULL`), which
     is invalid SQL. This blocks the `film` table.
  2. **`CREATE FUNCTION`** — only `CREATE PROCEDURE` is modeled, so the three
     Sakila stored functions (`get_customer_balance`, `inventory_in_stock`,
     `inventory_held_by_customer`) can't be built.

The integration tests in
`Squill.IntegrationTests/MariaDb/SakilaSample` track this: a build test runs
today, and the end-to-end deploy test is `Skip`ped with the exact reasons above.
When those features land, remove the skip and switch the test to the full schema.

Because it can't build cleanly yet, this project is **not** part of `Squill.slnx`.

## License

The schema is derived from the BSD-licensed Sakila database. See
[`LICENSE.txt`](LICENSE.txt) for the full attribution and license terms.
