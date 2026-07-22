-- The Sakila `film_text` table, kept in sync with `film` by the ins_film / upd_film / del_film
-- triggers (see Triggers/). It exists to exercise a MySQL FULLTEXT index: `FULLTEXT KEY`
-- builds an inverted index over the text columns so they can be searched with MATCH ... AGAINST
-- rather than only with LIKE.
--
-- Note this table uses a plain signed `int` primary key with no AUTO_INCREMENT — its `film_id`
-- is supplied by the triggers from `film`, not generated here — matching the canonical schema.
CREATE TABLE film_text
(
    film_id     int NOT NULL PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description text,
    FULLTEXT KEY idx_title_description (title, description)
);
