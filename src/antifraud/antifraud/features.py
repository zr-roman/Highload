"""Features of a payment, computed in one place for both the service and model training.

If training and serving computed them separately, they would drift apart (training/serving skew),
and the model would see in production something other than what it learned on.
"""

# TODO (model step): velocity per minute and per hour, amount vs the merchant's usual amount,
#      first payment of the merchant, hour of day.
