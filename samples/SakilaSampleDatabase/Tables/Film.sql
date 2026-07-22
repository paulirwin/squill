-- The Sakila `film` table — the richest table in the schema for MySQL/MariaDB-specific types.
-- It exercises several features found nowhere else in the sample:
--   * `year`    — the MySQL YEAR type (a 1-byte year value), used for `release_year`.
--   * `enum(...)` — a single-choice string type constrained to a fixed set of labels, here
--                 the MPAA `rating`.
--   * `set(...)`  — a multiple-choice string type: `special_features` may hold any subset of
--                 the listed labels in one column.
--   * `tinyint unsigned` / `smallint unsigned` — narrow unsigned integer types.
--   * `decimal(p,s)` columns with numeric DEFAULTs (`rental_rate`, `replacement_cost`).
--
-- Two foreign keys both point at `language`: `fk_film_language` for the spoken language and
-- `fk_film_language_original` for the original language (nullable). Sakila names the backing
-- indexes `idx_fk_language_id` and `idx_fk_original_language_id`.
CREATE TABLE film
(
    film_id              int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    title                varchar(255) NOT NULL,
    description          text,
    release_year         year,
    language_id          int unsigned NOT NULL,
    original_language_id int unsigned,
    rental_duration      tinyint unsigned NOT NULL DEFAULT 3,
    rental_rate          decimal(4, 2) NOT NULL DEFAULT 4.99,
    length               smallint unsigned,
    replacement_cost     decimal(5, 2) NOT NULL DEFAULT 19.99,
    rating               enum('G', 'PG', 'PG-13', 'R', 'NC-17') DEFAULT 'G',
    special_features     set('Trailers', 'Commentaries', 'Deleted Scenes', 'Behind the Scenes'),
    last_update          timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_film_language FOREIGN KEY (language_id) REFERENCES language (language_id),
    CONSTRAINT fk_film_language_original FOREIGN KEY (original_language_id) REFERENCES language (language_id)
);

CREATE INDEX idx_title ON film (title);
CREATE INDEX idx_fk_language_id ON film (language_id);
CREATE INDEX idx_fk_original_language_id ON film (original_language_id);
