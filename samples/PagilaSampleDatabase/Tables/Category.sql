-- Film categories (genres), joined to films through film_category.
CREATE TABLE category
(
    category_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        varchar(25) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);
