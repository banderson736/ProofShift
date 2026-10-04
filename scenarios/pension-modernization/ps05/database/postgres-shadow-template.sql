CREATE TABLE public.participant (
    id integer NOT NULL PRIMARY KEY,
    first_name text NOT NULL,
    status text NOT NULL,
    amount numeric(28, 8) NOT NULL,
    birth_date date NOT NULL,
    local_at timestamp without time zone NOT NULL,
    optional_value text NULL
);

CREATE TABLE public.member_status (
    id integer NOT NULL PRIMARY KEY,
    first_name text NOT NULL,
    status text NOT NULL,
    amount numeric(28, 8) NOT NULL,
    birth_date date NOT NULL,
    local_at timestamp without time zone NOT NULL,
    optional_value text NULL
);

CREATE TABLE public.supplemental_member (
    member_id text NOT NULL,
    pay_period text NOT NULL,
    note text NOT NULL,
    PRIMARY KEY (member_id, pay_period)
);