-- The Sakila `actor_info` view — the most advanced view in the schema. For each actor it lists,
-- per category, a comma-separated roll-up of that actor's film titles. This is built with a
-- correlated subquery inside a `GROUP_CONCAT`, itself feeding an outer `GROUP_CONCAT` that pairs
-- each category name with its film list — a nested-aggregate pattern specific to MySQL/MariaDB.
--
-- The canonical Sakila schema declares this view with `SQL SECURITY INVOKER`; that clause is
-- preserved here.
CREATE
    SQL SECURITY INVOKER
VIEW actor_info AS
SELECT a.actor_id,
       a.first_name,
       a.last_name,
       GROUP_CONCAT(DISTINCT CONCAT(c.name, ': ',
           (SELECT GROUP_CONCAT(f.title ORDER BY f.title SEPARATOR ', ')
            FROM film f
                INNER JOIN film_category fc ON f.film_id = fc.film_id
                INNER JOIN film_actor fa ON f.film_id = fa.film_id
            WHERE fc.category_id = c.category_id
              AND fa.actor_id = a.actor_id))
           ORDER BY c.name SEPARATOR '; ') AS film_info
FROM actor a
    LEFT JOIN film_actor fa ON a.actor_id = fa.actor_id
    LEFT JOIN film_category fc ON fa.film_id = fc.film_id
    LEFT JOIN category c ON fc.category_id = c.category_id
GROUP BY a.actor_id, a.first_name, a.last_name;
