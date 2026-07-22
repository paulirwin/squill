-- A user-defined ENUM type. PostgreSQL treats an enum as a first-class, ordered type whose
-- allowed values are fixed at declaration time. film.rating is typed as this enum below, so
-- the type is deployed before any table that references it.
CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');
