-- A denormalised view of each actor together with the categories they appear in and, per
-- category, the comma-separated list of films. Exercises a correlated subquery feeding
-- group_concat inside an outer group_concat, and the -> / grouping done in a subselect. One of
-- the more advanced Sakila views.
CREATE VIEW actor_info AS
SELECT a.actor_id,
       a.first_name,
       a.last_name,
       group_concat(DISTINCT
           c.name || ': ' ||
           (SELECT group_concat(f.title)
            FROM film f
                JOIN film_category fc ON f.film_id = fc.film_id
                JOIN film_actor fa ON f.film_id = fa.film_id
            WHERE fc.category_id = c.category_id
              AND fa.actor_id = a.actor_id)
       ) AS film_info
FROM actor a
    LEFT JOIN film_actor fa ON a.actor_id = fa.actor_id
    LEFT JOIN film_category fc ON fa.film_id = fc.film_id
    LEFT JOIN category c ON fc.category_id = c.category_id
GROUP BY a.actor_id, a.first_name, a.last_name;
