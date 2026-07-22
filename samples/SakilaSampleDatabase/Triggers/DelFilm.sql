-- The Sakila `del_film` trigger. Fires AFTER DELETE ON `film` and removes the corresponding
-- row from `film_text`, completing the trio of triggers that keep the FULLTEXT-indexed copy in
-- sync with `film`. Exercises an AFTER DELETE row-level trigger and the `OLD` pseudo-row.
CREATE TRIGGER del_film
    AFTER DELETE
    ON film
    FOR EACH ROW
BEGIN
    DELETE FROM film_text WHERE film_id = OLD.film_id;
END;
