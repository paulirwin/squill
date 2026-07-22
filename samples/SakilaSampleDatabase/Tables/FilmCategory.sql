-- The Sakila `film_category` junction table resolving the many-to-many relationship between
-- `film` and `category`. Like `film_actor`, it uses a composite PRIMARY KEY over its two
-- foreign-key columns.
CREATE TABLE film_category
(
    film_id     int unsigned NOT NULL,
    category_id int unsigned NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (film_id, category_id),
    CONSTRAINT fk_film_category_film FOREIGN KEY (film_id) REFERENCES film (film_id),
    CONSTRAINT fk_film_category_category FOREIGN KEY (category_id) REFERENCES category (category_id)
);
