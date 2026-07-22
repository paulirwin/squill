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

This sample is **aspirational**: it intentionally includes features Squill's
Postgres provider does not support yet, so it doubles as a living gap list. The
schema centres on the `film` table, which nearly every other table references
and which needs four unsupported features, so today the sample **does not build
or deploy** at all:

- **`CREATE TYPE ... AS ENUM`** — `mpaa_rating`, used by `film.rating`.
- **`CREATE DOMAIN`** with a `CHECK` — `year`, used by `film.release_year`.
- **`tsvector`** columns and their GiST index — `film.fulltext` /
  `film_fulltext_idx`.
- **Array columns** (`text[]`) — `film.special_features`.

Beyond `film`, it also uses PL/pgSQL and SQL functions, a user-defined aggregate,
and triggers, which are likewise not yet modeled.

The integration tests in
`Squill.IntegrationTests/Postgres/PagilaSampleTest` track this: a passing test
asserts the full schema still fails to build (so it will start failing — a useful
signal — once the features land), and the end-to-end deploy test is `Skip`ped
with the exact reasons above.

Because it can't build yet, this project is **not** part of `Squill.slnx`.

## License

The schema is derived from the BSD-licensed Sakila / Pagila database. See
[`LICENSE.txt`](LICENSE.txt) for the full attribution and license terms.
