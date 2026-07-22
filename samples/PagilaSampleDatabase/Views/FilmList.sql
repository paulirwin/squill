-- A catalogue listing of films with their category and a comma-separated cast, built with the
-- user-defined group_concat aggregate (see Programmability/GroupConcat.sql). The aggregate is
-- deployed before this view. Filters to films rated below R for a "general" listing.
CREATE VIEW film_list AS
SELECT film.film_id AS fid,
       film.title,
       film.description,
       category.name AS category,
       film.rental_rate AS price,
       film.length,
       film.rating,
       group_concat(actor.first_name || ' ' || actor.last_name) AS actors
FROM film
    LEFT JOIN film_category ON film.film_id = film_category.film_id
    LEFT JOIN category ON film_category.category_id = category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, film.title, film.description, category.name,
         film.rental_rate, film.length, film.rating;
