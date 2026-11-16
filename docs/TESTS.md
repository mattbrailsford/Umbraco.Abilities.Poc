# What the tests show

> Part of the [Abilities proof of concept](../README.md). Each claim is followed by the tests that show it. `(all)`
> means every test in that class. Run them with `dotnet test`.

The in-memory tests (`Umbraco.Abilities.Tests`) drive the samples through the AI tools with JSON, the way a model
would. The CMS tests (`Umbraco.Abilities.Cms.Tests`) do the same against a real Umbraco 18 on SQLite.

## Building from text

- A discount is built from text step by step, nothing is live until apply, and the result is stored
  - `CreationPromptTests.Discount_from_text_is_built_in_a_draft_set_and_only_goes_live_when_applied`
- Problem paths are the same paths the edit abilities take, so a model can fix its own work
  - `CreationPromptTests.Validation_reports_problems_by_path_and_the_agent_fixes_them_with_the_same_paths`
- Bad edits are correctable Input failures with a path; edits never run outside a set
  - `CreationPromptTests.Bad_edits_are_correctable_input_failures_with_paths`
- A third party's rule type, written as a plain Commerce provider with no Abilities code, is creatable from text
  - `CreationPromptTests.A_third_party_rule_type_is_creatable_with_no_change_to_commerce`
- Every ability becomes an AI tool; only irreversible Writes are wrapped for approval
  - `CreationPromptTests.Every_ability_is_an_AI_tool_and_only_irreversible_writes_need_approval`

## Change sets

- One set holds a page with nested blocks, a discount referring to that new page and switched on, and an email to send afterwards; activating the discount is a change shown in its working state, the email runs after every change
  - `ChangeSetTests.A_campaign_spanning_content_commerce_and_a_side_effect_is_applied_together`
- A failed step leaves earlier products' changes applied; applying again resumes at the failed step and applies nothing twice
  - `ChangeSetTests.A_failed_step_leaves_earlier_steps_applied_and_applying_again_resumes`
- A product whose save fails rolls back all of its changes in the set, which can then be fixed and resumed
  - `ChangeSetTests.A_product_whose_save_fails_rolls_back_all_of_its_changes_and_can_be_fixed_and_resumed`
- Reads inside a set show its edits; reads outside show live data
  - `ChangeSetTests.Reads_inside_a_set_show_its_edits_and_reads_outside_show_live_data`
- A product's own Read ability shows what the current set changed and created, and live data outside it
  - `ChangeSetTests.A_product_read_ability_shows_the_current_sets_changes_and_live_data_outside_it`
- Replay is deterministic, and keys handed to the model are the stored keys (for products that store item ids; see Commerce's derived keys)
  - `ChangeSetTests.Replaying_a_set_gives_the_same_entity_every_time`
- A call works in the change set it names, or the only one open in the conversation. With two or more open, it must
  name one. No ability takes the id: the AI adapter adds an optional `changeSetId` to every tool that uses a set, and the set is ambient
  for that call only
  - `CurrentChangeSetTests` (all)
- An HTTP request names its change set in the `Umb-Change-Set` header; an unknown set is refused before the request runs
  - `CmsDocumentAdapterTests.A_request_naming_a_change_set_in_its_header_works_in_that_set_for_that_request_only`
  - `CmsDocumentAdapterTests.A_request_naming_an_unknown_change_set_is_refused_before_it_runs`
  - `CmsDocumentAdapterTests.A_request_without_the_header_works_in_no_change_set`

## Rebasing and conflicts

- Changes saved elsewhere meanwhile that don't overlap the set's are rebased onto automatically, and both kept
  - `ChangeSetTests.Changes_saved_elsewhere_that_do_not_overlap_are_rebased_onto_automatically`
- A value both changed is a conflict, with both values shown, until one is kept
  - `ChangeSetTests.A_value_both_changed_is_a_conflict_until_one_is_kept`
  - `ChangeSetTests.Keeping_yours_overwrites_their_value`
- Editing an item someone else removed is a conflict that can only keep theirs; the set's other edits still apply
  - `ChangeSetTests.Editing_an_item_someone_else_removed_is_a_conflict_that_can_only_keep_theirs`
- The same change made on both sides is not a conflict
  - `ChangeSetTests.The_same_change_made_on_both_sides_is_not_a_conflict`
- An entity whose product keeps version history is rebased from that history, with no snapshot in the set
  - `ChangeSetTests.An_entity_with_version_history_is_rebased_from_its_history_without_a_snapshot`
- If the starting version is gone from the history, the entity can't be rebased, and says why
  - `ChangeSetTests.If_the_starting_version_is_gone_from_the_history_the_entity_cant_be_rebased`

## Typed models

- A class with no attributes and no base class is described fluently, and created from text like any other
  - `TypedModelTests.A_class_with_no_attributes_or_base_class_is_described_fluently`
  - `TypedModelTests.A_fluently_described_entity_is_created_from_text_like_any_other`
- Commerce keeps its own model; the adapter maps to it, and rule keys are derived because Commerce rules have no ids
  - `TypedModelTests.Commerce_keeps_its_own_model_and_the_definition_maps_to_it`
- A new entity starts with its model's `[DefaultValue]`s, which the schema shows
  - `TypedModelTests.A_new_entity_starts_with_its_models_default_values`
- A property's C# initializer is a default too, filled inside a new item's objects (a grid block's row span)
  - `TypedModelTests.A_property_initializer_is_a_default_too_inside_a_new_items_objects`
- The schema (types, required, allowed values, references, items fields, variation, settings, placement, JSON Schema) comes from model classes and attributes
  - `TypedModelTests.The_structure_comes_from_the_model_class_and_its_attributes`
  - `TypedModelTests.Varying_values_settings_layout_and_json_values_are_described_too`
- A model round-trips through its working copy, keeping item keys and giving each item its path
  - `TypedModelTests.A_model_round_trips_through_its_document_with_item_keys_and_paths`
- Each problem is reported once, whether it comes from the schema, an attribute or a domain rule
  - `TypedModelTests.Each_problem_is_reported_once_whether_it_comes_from_the_structure_or_an_attribute`
- Item types come from an items list's element type, with nothing registered by hand, and can be narrowed by naming classes
  - `TypedModelTests.Item_types_come_from_the_lists_element_type_and_can_be_narrowed_by_naming_classes`
- A field's allowed item types can come from a setting read at run time, as a Block List's data type does: types left out aren't offered and can't be added
  - `TypedModelTests.A_fields_allowed_item_types_can_come_from_a_setting_read_at_run_time`
- Item type sources, allow lists and availability can wait, and are read once per call
  - `EntityTests.Sources_allow_lists_and_availability_can_wait_and_are_read_once_per_call`
- An edit ability finds the entity's key from its input's `[EntityKey]` property, and says what to add when there is none
  - `TypedModelTests.An_edit_ability_finds_its_key_from_the_inputs_entity_key_property`

## CMS draft sets

- Content changes in a change set are a CMS draft set: the backoffice can read it, it records only what changed, and discarding the set discards it
  - `CmsDocumentAdapterTests.Content_changes_in_a_change_set_are_a_CMS_draft_set_that_records_only_what_changed`
- The CMS draft set works on its own, with no abilities: stage, read through, conflict, resolve, validate, apply
  - `CmsDocumentAdapterTests.A_draft_set_works_on_its_own_with_no_abilities`
- A package can veto applying a change set or its CMS draft set with a cancelable notification, and both raise applied notifications; applying writes to the CMS audit log
  - `CmsDocumentAdapterTests.A_package_can_veto_applying_a_change_set_or_its_draft_set_with_a_CMS_notification`
  - `CmsDocumentAdapterTests.A_draft_set_works_on_its_own_with_no_abilities`
- A draft set applied in a scope that then rolls back isn't applied and raises no applied notification
  - `CmsDocumentAdapterTests.A_draft_set_applied_inside_a_scope_that_rolls_back_is_not_applied_and_raises_nothing`

## Identity, permissions and retries

- Entity types are UDI entity types, package types are registered, and references can be given as UDIs, stored in the field's own form, with the wrong type refused
  - `UdiTests (all)`
- An agent gets the permissions of the user it acts for: recording, applying (checked for the applying user, nothing applied if any change is refused), finding, reading and direct runs; with the CMS's real document permissions
  - `AuthorizationTests (all)`
- An adapter with no access check can't be registered
  - `AuthorizationTests.An_adapter_that_does_not_check_access_cannot_be_registered`
  - `CmsDocumentAdapterTests.An_agent_gets_the_CMS_permissions_of_the_user_it_acts_for`
- A side effect with retries is queued after apply and retried with backoff; it gives up after its limit; correctable input isn't retried; on real Umbraco it runs on the background queue
  - `SideEffectRetryTests (all)`
  - `CmsDocumentAdapterTests.A_side_effect_with_retries_runs_on_Umbracos_background_queue_after_the_content_commits`

## Plugin types and editor values

- Rule and reward types come from Commerce's provider collections, with the label fallback and editor references; a provider registered later shows up; a group rule nests children and is stored as Commerce's rule tree
  - `PluginItemTypeTests (all)`
- Automate's triggers and actions come from its collections, settings can be bindings, and users are only offered the types they may use
  - `PluginItemTypeTests.Automates_triggers_and_actions_come_from_its_collections_and_settings_can_be_bindings`
  - `PluginItemTypeTests.A_user_is_only_offered_plugin_types_they_may_use`
- A value whose model is its editor's (Rich Text's markup plus blocks) is edited inside through its handler, with the same tools; the handler keeps markup and blocks in step and reports its problems on paths inside the value
  - `ValueHandlerTests (all)`
- Editors the CMS adapter has no rule for are fields described by Umbraco's own value schemas
  - `CmsDocumentAdapterTests.Editors_the_definition_has_no_rule_for_are_fields_described_by_Umbracos_value_schemas`
- A third party's property editor with its own handler, found on its own, is the field its handler describes (a star rating as a number from 1 to 5), checked before Umbraco sees it, saved in the editor's own shape and read back
  - `CmsDocumentAdapterTests.A_third_party_editor_with_a_handler_is_a_field_shaped_by_that_handler`
- Two products with drafts in one transaction each keep their own draft; both are applied, and discarding the set discards both
  - `DraftingTests (all)`
- Documents and media in one set are applied together in one Umbraco scope, and a refused document rolls back the media saved with it
  - `CmsDocumentAdapterTests.Documents_and_media_in_one_set_are_applied_together_in_one_Umbraco_scope`
  - `CmsDocumentAdapterTests.A_refused_document_rolls_back_the_media_saved_with_it`
- Media is built on the same base class as documents, and gets the same editors, nested blocks, error paths and defaults with no code of its own
  - `CmsDocumentAdapterTests.Media_is_built_on_the_same_base_with_the_same_editors_blocks_and_defaults`
- A new page and a new block start with their editors' presets, as the backoffice would start them (a toggle's default, a number's minimum)
  - `CmsDocumentAdapterTests.A_new_page_and_a_new_block_start_with_their_editors_presets`

## Workflows

- Workflow step types are derived from abilities
  - `WorkflowTests.Step_types_are_derived_from_abilities`
- A workflow graph (edges as item references) is validated, and can refer to a discount created in the same set
  - `WorkflowTests.A_workflow_from_text_links_its_steps_and_can_refer_to_a_discount_in_the_same_set`
- A step that names outcomes has one exit per outcome in `outcomes`, and no `next`; a wrong outcome is refused
  - `WorkflowTests.A_step_that_names_outcomes_has_one_exit_per_outcome_and_no_next`
- A branching workflow is saved as Automate's connections, each with its outcome, and loaded back as exits
  - `WorkflowTests.A_branching_workflow_is_saved_as_Automates_connections_with_outcomes_and_loaded_back_as_exits`

## Cultures and blocks

- Names and values vary by culture; invariant values are shared across cultures
  - `CmsContentTests.Names_and_values_vary_by_culture_while_invariant_values_are_shared`
- A value that varies by culture needs a known culture; an entity with no culture can't be applied
  - `CmsContentTests.A_value_that_varies_by_culture_needs_a_known_culture`
- Each culture is validated separately, and problems say which culture
  - `CmsContentTests.Each_culture_is_validated_separately_and_problems_say_which`
- A value can vary by two dimensions at once (culture and segment). Each segment keeps its own value, a segment still needs a known culture, and problems name both
  - `EntityTests.A_value_can_vary_by_two_dimensions_and_each_segment_keeps_its_own_value`
  - `EntityTests.A_segment_still_needs_a_known_culture`
  - `EntityTests.Problems_in_a_segment_say_which_culture_and_segment`
- Blocks in an invariant Block List hold per-culture values, and a block's settings are an object edited and validated at `{item}/settings/…`
  - `CmsContentTests.Block_values_vary_per_culture_inside_an_invariant_block_list_and_settings_are_an_object`
- Block Grid: column span is in a layout object at `{item}/layout/…`, areas are items fields with limits, and a type can be allowed at the root and in areas
  - `CmsContentTests.Grid_blocks_have_a_layout_and_areas_that_are_items_fields`
- A culture-variant block property holds a separate list per culture
  - `CmsContentTests.A_culture_variant_block_property_holds_a_list_per_culture`
- A media picker and a multi-type picker refer to media and pages created in the same set
  - `CmsContentTests.Pickers_refer_to_media_and_pages_created_in_the_same_set`

## Against a real Umbraco

- The schema comes from the document type: cultures, varying fields, block types per items field, settings, grid areas and placement
  - `CmsDocumentAdapterTests.The_schema_comes_from_the_document_type_and_its_block_editors`
- A Block List offers the blocks its data type allows now: a block taken out of the data type stops being offered and can't be added, with no restart, and a page that already has one still loads, edits and is reported by validation
  - `CmsDocumentAdapterTests.A_block_list_offers_the_blocks_its_data_type_allows_now`
- A culture-variant page with a Block List (per-culture block values and a settings element) and a Block Grid (column span, areas) built through the AI tools is stored by Umbraco as expected, and loads back to the same shape
  - `CmsDocumentAdapterTests.A_page_built_from_text_is_saved_by_Umbraco_with_its_blocks_settings_grid_and_cultures`
- Umbraco's validation errors come back on the entity paths and cultures, and the generic check agrees with Umbraco
  - `CmsDocumentAdapterTests.Umbraco_validation_problems_come_back_on_the_entity_paths_and_cultures`
- Editing an existing page keeps what the edit didn't touch (other cultures, settings, the grid)
  - `CmsDocumentAdapterTests.Editing_an_existing_page_keeps_everything_the_edit_did_not_touch`
- A page refused on save (by a notification handler) rolls back the page saved before it in the same Umbraco scope
  - `CmsDocumentAdapterTests.A_failed_save_rolls_back_every_document_in_the_Umbraco_scope`
- A side effect failing after Content's step leaves the content applied, and resuming doesn't save it again
  - `CmsDocumentAdapterTests.A_failed_side_effect_after_the_content_leaves_the_content_applied_and_the_set_resumable`
- A backoffice change made meanwhile (Danish subtitle) is rebased onto; the set's block edit applies on top and both are kept
  - `CmsDocumentAdapterTests.Changes_saved_in_the_backoffice_meanwhile_are_rebased_onto_when_they_do_not_overlap`
- A value changed in both the set and the backoffice is a conflict, with its culture, until one is kept
  - `CmsDocumentAdapterTests.A_value_changed_in_both_the_set_and_the_backoffice_is_a_conflict_to_resolve`
- Values that vary by culture and segment are saved per segment, in the page and inside a block, and load back the same
  - `CmsDocumentAdapterTests.Values_that_vary_by_culture_and_segment_are_saved_per_segment_and_load_back`
  - `CmsDocumentAdapterTests.Block_values_that_vary_by_segment_are_saved_in_the_block_and_load_back`
- A required value left empty in a segment is a problem in that segment, from Umbraco and from the generic check alike
  - `CmsDocumentAdapterTests.A_required_value_missing_in_a_segment_is_a_problem_in_that_segment_as_Umbraco_reports_it`
- A value that varies by segment only is shared by every culture, saved per segment, and a missing one is a problem with no culture
  - `CmsDocumentAdapterTests.A_value_that_varies_by_segment_only_is_shared_by_every_culture_and_saved_per_segment`
  - `CmsDocumentAdapterTests.A_required_segment_only_value_missing_in_a_segment_is_a_problem_with_no_culture`
