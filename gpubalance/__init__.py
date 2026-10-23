"""Launch-time workload balancing across mixed-vendor GPUs on Linux."""

import logging

__version__ = "0.5.0"

# Library use stays quiet until the CLI (or the caller) sets up logging.
logging.getLogger(__name__).addHandler(logging.NullHandler())
