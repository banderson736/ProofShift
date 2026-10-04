CREATE TABLE public.participant (
    participant_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    member_number varchar(12) NOT NULL UNIQUE,
    display_name text NOT NULL,
    membership_state varchar(20) NOT NULL,
    date_of_birth date NOT NULL,
    joined_on date NOT NULL
);

CREATE TABLE public.employment_event (
    event_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    event_date date NOT NULL,
    event_code varchar(24) NOT NULL,
    source_employment_ref varchar(16) NOT NULL
);

CREATE TABLE public.contribution_transaction (
    contribution_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_transaction_ref varchar(20) NOT NULL UNIQUE,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    payroll_period char(7) NOT NULL,
    contribution_kind varchar(24) NOT NULL,
    amount numeric(19, 4) NOT NULL
);

CREATE TABLE public.service_period (
    service_period_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    service_window daterange NOT NULL,
    credited_service numeric(12, 6) NOT NULL,
    credit_code varchar(24) NOT NULL
);

CREATE TABLE public.beneficiary_relationship (
    relationship_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    beneficiary_ref varchar(16) NOT NULL,
    relationship_type varchar(24) NOT NULL,
    allocation_percent numeric(7, 4) NOT NULL
);

CREATE TABLE public.retirement_election (
    election_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_election_ref varchar(16) NOT NULL UNIQUE,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    option_code varchar(32) NOT NULL,
    effective_on date NOT NULL
);

CREATE TABLE public.benefit_payment (
    payment_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_payment_ref varchar(20) NOT NULL UNIQUE,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    payment_period char(7) NOT NULL,
    paid_on date NOT NULL,
    amount numeric(19, 4) NOT NULL
);

CREATE TABLE public.document_object_index (
    object_key bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_document_ref varchar(20) NOT NULL UNIQUE,
    participant_key bigint NOT NULL REFERENCES public.participant(participant_key),
    object_path text NOT NULL,
    document_kind varchar(24) NOT NULL,
    content_sha256 char(64) NOT NULL
);
