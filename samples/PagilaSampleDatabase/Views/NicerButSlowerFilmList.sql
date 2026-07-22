-- Same shape as film_list, but the cast names are title-cased for a "nicer" (and, as the name
-- warns, slower) presentation. Also built on the group_concat aggregate.
CREATE VIEW nicer_but_slower_film_list AS
SELECT film.film_id AS fid,
       film.title,
       film.description,
       category.name AS category,
       film.rental_rate AS price,
       film.length,
       film.rating,
       group_concat(
           upper(substring(actor.first_name, 1, 1)) || lower(substring(actor.first_name, 2)) || ' ' ||
           upper(substring(actor.last_name, 1, 1)) || lower(substring(actor.last_name, 2))
       ) AS actors
FROM film
    LEFT JOIN film_category ON film.film_id = film_category.film_id
    LEFT JOIN category ON film_category.category_id = category.category_id
    LEFT JOIN film_actor ON film.film_id = film_actor.film_id
    LEFT JOIN actor ON film_actor.actor_id = actor.actor_id
GROUP BY film.film_id, film.title, film.description, category.name,
         film.rental_rate, film.length, film.rating;
