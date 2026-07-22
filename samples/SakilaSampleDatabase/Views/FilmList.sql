-- The Sakila `film_list` view. Demonstrates `GROUP_CONCAT` (a MySQL/MariaDB aggregate that
-- concatenates the grouped actor names into a single string) together with a multi-table join
-- through the film_category / category and film_actor / actor junctions, grouped per film.
CREATE VIEW film_list AS
SELECT film.film_id                                        AS FID,
       film.title                                          AS title,
       film.description                                    AS description,
       category.name                                       AS category,
       film.rental_rate                                    AS price,
       film.length                                         AS length,
       film.rating                                         AS rating,
       GROUP_CONCAT(CONCAT(actor.first_name, ' ', actor.last_name) SEPARATOR ', ') AS actors
FROM film
    LEFT JOIN film_category ON film_category.film_id = film.film_id
    LEFT JOIN category ON category.category_id = film_category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, category.name;
