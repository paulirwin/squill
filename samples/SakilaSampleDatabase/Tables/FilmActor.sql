-- The Sakila `film_actor` junction table resolving the many-to-many relationship between
-- `film` and `actor`. Demonstrates a composite PRIMARY KEY over the two foreign-key columns
-- (there is no surrogate key here), plus named FKs to each parent and the Sakila-convention
-- backing index on the second FK column.
CREATE TABLE film_actor
(
    actor_id    int unsigned NOT NULL,
    film_id     int unsigned NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (actor_id, film_id),
    CONSTRAINT fk_film_actor_actor FOREIGN KEY (actor_id) REFERENCES actor (actor_id),
    CONSTRAINT fk_film_actor_film FOREIGN KEY (film_id) REFERENCES film (film_id)
);

CREATE INDEX idx_fk_film_id ON film_actor (film_id);
