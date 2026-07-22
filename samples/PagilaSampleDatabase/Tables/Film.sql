-- The catalogue of films. This is the richest table in the schema and exercises several
-- advanced PostgreSQL features:
--   * release_year is typed as the "year" DOMAIN (see Types/Year.sql), so its range check
--     travels with the type.
--   * rating is the "mpaa_rating" ENUM (see Types/MpaaRating.sql), defaulting to 'G'.
--   * special_features is a text ARRAY (text[]).
--   * fulltext is a tsvector, kept current by the film_fulltext_trigger and searched through
--     the GiST index below.
-- Two self-referential-style foreign keys point at language: the spoken language and the
-- optional original language.
CREATE TABLE film
(
    film_id              integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title                varchar(255) NOT NULL,
    description          text,
    release_year         year,
    language_id          integer NOT NULL REFERENCES language (language_id),
    original_language_id integer REFERENCES language (language_id),
    rental_duration      smallint NOT NULL DEFAULT 3,
    rental_rate          numeric(4,2) NOT NULL DEFAULT 4.99,
    length               smallint,
    replacement_cost     numeric(5,2) NOT NULL DEFAULT 19.99,
    rating               mpaa_rating DEFAULT 'G',
    last_update          timestamp NOT NULL DEFAULT now(),
    special_features     text[],
    fulltext             tsvector NOT NULL
);

CREATE INDEX idx_title ON film (title);
CREATE INDEX idx_fk_language_id ON film (language_id);
CREATE INDEX idx_fk_original_language_id ON film (original_language_id);

-- A GiST index over the tsvector column enables fast full-text search on the film's fulltext.
CREATE INDEX film_fulltext_idx ON film USING gist (fulltext);
