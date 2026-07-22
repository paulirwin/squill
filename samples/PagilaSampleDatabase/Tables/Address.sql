-- Street addresses, each tied to a city. Nullable columns (address2, postal_code) model
-- optional address parts; the inline REFERENCES declares the foreign key to city.
CREATE TABLE address
(
    address_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    address     varchar(50) NOT NULL,
    address2    varchar(50),
    district    varchar(20) NOT NULL,
    city_id     integer NOT NULL REFERENCES city (city_id),
    postal_code varchar(10),
    phone       varchar(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT now()
);

CREATE INDEX idx_fk_city_id ON address (city_id);
