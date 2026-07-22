-- Junction table linking films to categories, with a composite (film_id, category_id)
-- primary key declared at table level.
CREATE TABLE film_category
(
    film_id     integer NOT NULL REFERENCES film (film_id),
    category_id integer NOT NULL REFERENCES category (category_id),
    last_update timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (film_id, category_id)
);
