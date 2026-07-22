-- The Sakila `ins_film` trigger. Fires AFTER INSERT ON `film` and copies the new film's
-- searchable text (title, description) into the `film_text` table, keeping the FULLTEXT-indexed
-- copy in sync. Exercises an AFTER INSERT row-level trigger and the `NEW` pseudo-row.
CREATE TRIGGER ins_film
    AFTER INSERT
    ON film
    FOR EACH ROW
BEGIN
    INSERT INTO film_text (film_id, title, description)
    VALUES (NEW.film_id, NEW.title, NEW.description);
END;
