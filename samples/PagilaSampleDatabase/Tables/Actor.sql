-- The actors that appear in films. A surrogate identity key stands in for the natural key,
-- and last_update is stamped on every row change by the last_updated trigger (see Triggers/).
CREATE TABLE actor
(
    actor_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_actor_last_name ON actor (last_name);
