# Pagila sample database (PostgreSQL)

A Squill port of [Pagila](https://github.com/jOOQ/sakila) — the PostgreSQL
version of the classic Sakila DVD-rental schema, PostgreSQL's answer to SQL
Server's AdventureWorks — expressed as declarative `.sql` files, one object per
file. It is a deliberately ambitious, production-style schema meant to exercise
the Postgres provider well beyond the small
[PostgresSampleDatabase](../PostgresSampleDatabase).

What it covers: an `ENUM` type and a `DOMAIN`, identity keys and foreign keys
(including the circular `staff`↔`store` pair), multi-column and GiST indexes,
`tsvector` full-text search, array columns, PL/pgSQL and SQL functions, a
user-defined aggregate, and triggers.

## Status

The full sample **builds into a DACPAC and deploys against real Postgres**. The
features it centres on — around the `film` table and beyond — are all modeled:

- **`CREATE TYPE ... AS ENUM`** (`mpaa_rating`, used by `film.rating`) and
  **`CREATE DOMAIN`** with a `CHECK` (`year`, used by `film.release_year`) —
  issues #75 / #80.
- **`tsvector`** columns and their GiST index (`film.fulltext` /
  `film_fulltext_idx`) and **array columns** (`text[]`, `film.special_features`)
  — issue #76.
- PL/pgSQL and SQL **functions** (#81), a user-defined **aggregate**
  (`group_concat`, #82), and **triggers** (#83).

The integration tests in
`Squill.IntegrationTests/Postgres/PagilaSampleTest` cover this end to end: a
DB-less `BuildFullSchema_Succeeds` test builds the DACPAC, and
`Deploy_PagilaSample_ProducesTheSampleSchema` deploys it into a real Postgres
database (via the same code path as `squill deploy`) and asserts that
representative objects across the feature surface — the `film` table, the
`actor_info` view, the `group_concat` aggregate, and the `film_fulltext_trigger`
— exist afterward. Both tests are live (not skipped).

This project is part of `Squill.slnx`, so it builds with the solution. The same
schema is also embedded as a resource in `Squill.IntegrationTests`, which is how
the end-to-end deploy tests above exercise it against a real database.

## License

The schema is derived from the BSD-licensed Sakila / Pagila database. See
[`LICENSE.txt`](LICENSE.txt) for the full attribution and license terms.
