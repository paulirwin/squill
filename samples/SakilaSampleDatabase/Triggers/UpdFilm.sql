-- The Sakila `upd_film` trigger. Fires AFTER UPDATE ON `film` and, when any of the searchable
-- columns or the primary key change, propagates the change into `film_text`. Exercises an
-- AFTER UPDATE row-level trigger using both the `OLD` and `NEW` pseudo-rows inside an IF guard.
CREATE TRIGGER upd_film
    AFTER UPDATE
    ON film
    FOR EACH ROW
BEGIN
    IF (OLD.title != NEW.title)
        OR (OLD.description != NEW.description)
        OR (OLD.film_id != NEW.film_id)
    THEN
        UPDATE film_text
        SET title       = NEW.title,
            description = NEW.description,
            film_id     = NEW.film_id
        WHERE film_id = OLD.film_id;
    END IF;
END;
