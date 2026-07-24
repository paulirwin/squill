# Sakila sample database (MariaDB / MySQL)

A Squill port of the classic [Sakila](https://github.com/jOOQ/sakila) DVD-rental
schema — the MySQL counterpart to SQL Server's AdventureWorks — expressed as
declarative `.sql` files, one object per file. It is a deliberately ambitious,
production-style schema meant to exercise the MariaDB provider well beyond the
small [MariaDbSampleDatabase](../MariaDbSampleDatabase).

What it covers: 16 tables with `AUTO_INCREMENT` keys and foreign keys (including
the circular `staff`↔`store` pair), `ENUM` / `SET` / `YEAR` columns,
`ON UPDATE CURRENT_TIMESTAMP`, a `FULLTEXT` index, six views, stored
procedures, stored functions, and triggers.

## Status

The full sample **builds into a DACPAC and deploys against real MariaDB and
MySQL**. The features it exercises are all modeled:

- **`ENUM` / `SET` script generation** — the value lists are preserved in the
  generated DDL (issue #73), so the `film` table's `rating` enum and
  `special_features` set deploy correctly.
- **`CREATE FUNCTION`** — the three Sakila stored functions
  (`get_customer_balance`, `inventory_in_stock`, `inventory_held_by_customer`)
  are modeled alongside the procedures (issue #74).
- **Triggers** — the three `film` triggers that keep the `FULLTEXT`-indexed
  `film_text` copy in sync (issue #100).

The integration tests in
`Squill.IntegrationTests/MariaDb/SakilaSample` cover this end to end, running
once against MariaDB and once against MySQL: a DB-less
`BuildFullSchema_ProducesADacpac` test builds the DACPAC, and
`Deploy_SakilaSample_ProducesTheSampleSchema` deploys it into a real database
(via the same code path as `squill deploy`) and asserts that a representative
object from each feature area — the `film` table, its `rating` enum column, the
`customer_list` view, the `get_customer_balance` function, the `film_in_stock`
procedure, and the `ins_film` trigger — exists afterward. Both tests are live
(not skipped).

This project is part of `Squill.slnx`, so it builds with the solution. The same
schema is also embedded as a resource in `Squill.IntegrationTests`, which is how
the end-to-end deploy tests above exercise it against a real database.

## License

The schema is derived from the BSD-licensed Sakila database. See
[`LICENSE.txt`](LICENSE.txt) for the full attribution and license terms.
