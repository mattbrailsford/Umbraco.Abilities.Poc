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
- Change sets made by many conversations at once are all kept
  - `CurrentChangeSetTests.Change_sets_created_by_many_conversations_at_once_are_all_kept`
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
- A typed adapter can override a save that is also given the model as it is live (none for a new one), in a change set and run directly
  - `TypedModelTests.A_typed_adapter_can_be_given_the_model_as_it_is_live_when_it_saves`

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

## Delete and move

- `entity.getSchema` lists what a type can do beyond setting values: delete and move, when its adapter opts in
  - `EntityCapabilityTests.The_schema_lists_what_an_entity_type_can_do_beyond_setting_values`
- A deleted entity is gone inside the set (get, find, further edits), still live outside it, summarized as deleted, and deleted only on apply
  - `EntityCapabilityTests.A_deleted_entity_is_gone_in_the_set_and_deleted_only_when_the_set_is_applied`
- An entity created and deleted in one set is never saved
  - `EntityCapabilityTests.An_entity_created_and_deleted_in_one_set_is_never_saved`
- An entity someone else deleted is not a conflict when the set deletes it too
  - `EntityCapabilityTests.An_entity_someone_else_deleted_is_not_a_conflict_when_the_set_deletes_it_too`
- On apply, saves come before deletes, so a child moved out of a deleted parent is moved first
  - `EntityCapabilityTests.Children_moved_out_of_a_deleted_parent_are_saved_before_it_is_deleted`
- An entity type whose adapter didn't opt in can't be deleted or moved: a problem on `entityType`
  - `EntityCapabilityTests.Entity_types_whose_adapters_did_not_opt_in_cannot_be_deleted_or_moved`
- A move is checked as a move, not an update, and setting the parent with `entity.setValue` is checked the same way
  - `EntityCapabilityTests.A_move_is_checked_as_a_move_and_setting_the_parent_directly_is_too`
  - `EntityCapabilityTests.An_entity_cannot_be_moved_under_itself`
- An adapter's save is given the entity as it is live (none for a new one), so it can see what changed, such as a new parent
  - `EntityCapabilityTests.Save_is_given_the_entity_as_it_is_live_so_the_adapter_sees_what_changed`
## Plugin types and editor values

- Rule and reward types come from Commerce's provider collections, with the label fallback and editor references; a provider registered later shows up; a group rule nests children and is stored as Commerce's rule tree
  - `PluginItemTypeTests (all)`
- A model sees plugin settings in the framework's JSON, whatever the product stores: .NET's own form by default (enum member names, numbers as numbers), or the product's own format; a stored value the type can't read is shown as stored
  - `PluginSettingFormatTests (all)`
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

## Over the Management API

Called over HTTP on Umbraco's test server, as the Umbraco CMS MCP Dev server would call them.

- The framework's tool schemas, from .NET's own exporter, are the ones M.E.AI would make, for every sample ability
  - `AbilityToolSchemaTests.Every_abilitys_input_schema_is_the_one_MEAI_would_make`
- A call that can't be undone runs only with the approval made for it, decided per call: recorded in a change set it needs no consent
  - `AbilityToolConsentTests.A_call_that_cannot_be_undone_runs_only_with_the_approval_made_for_it`
  - `AbilityToolConsentTests.A_call_that_can_be_undone_needs_no_consent`
  - `AbilityToolConsentTests.A_call_that_needs_consent_live_needs_none_when_it_is_only_recorded_in_a_change_set`
  - `AbilityToolConsentTests.A_surface_that_asked_the_person_itself_needs_no_approval_id`
- An approval approves only its own call, by the person asked, once; several can wait at once
  - `AbilityToolConsentTests.An_approval_approves_only_its_own_call_and_only_once_so_several_can_wait_at_once`
  - `AbilityToolConsentTests.An_approval_is_for_the_person_who_was_asked`
- Asking to apply a set shows what it holds, names what couldn't be undone on its own, and asks again if the set changed
  - `AbilityToolConsentTests.Asking_to_apply_a_set_shows_what_it_holds_and_names_what_could_not_be_undone_on_its_own`
  - `AbilityToolConsentTests.An_approval_for_a_change_set_is_refused_once_the_set_changed_and_the_person_is_asked_again`

- The list endpoint gives every ability with what a tool needs: tool name, description, kind, and the input schema with `changeSetId` when it uses a change set
  - `AbilitiesManagementApiTests.The_Management_API_lists_every_ability_with_what_a_tool_needs`
- A page built over HTTP in a change set named in the `Umb-Change-Set` header is saved only on apply, as the signed-in user
  - `AbilitiesManagementApiTests.A_caller_builds_a_page_in_a_change_set_named_in_a_header_and_it_is_saved_as_that_user_on_apply`
- A mistake the caller can fix is a 400 with problems, and says which change set it used
  - `AbilitiesManagementApiTests.A_mistake_the_caller_can_fix_is_a_400_with_problems_by_path`
- A user without the Content section is refused with a 403
  - `AbilitiesManagementApiTests.A_user_without_the_Content_section_is_refused_with_a_403`
- A header naming an unknown change set is refused, and nothing is written live
  - `AbilitiesManagementApiTests.A_header_naming_an_unknown_change_set_is_refused_rather_than_writing_live`
- A call that can't be undone is a 428 with a question and an approval id, and does nothing; sent again with that id in `Umb-Approval`, it runs. A made-up id approves nothing
  - `AbilitiesManagementApiTests.A_call_that_cannot_be_undone_is_a_428_with_a_question_and_runs_with_that_approvals_id`
- A caller that isn't signed in is refused
  - `AbilitiesManagementApiTests.A_caller_that_is_not_signed_in_is_refused`

Through MCP, as MCP Dev would: an MCP client calls the proxy in `tools/Umbraco.Abilities.McpProxy`, which calls the
endpoints.

- An MCP client gets one tool per ability the site lists, with its description and input schema
  - `AbilitiesMcpProxyTests.An_MCP_client_gets_one_tool_per_ability_the_site_lists`
- An MCP client is told which tools only read, and which can't be undone and need a person to approve them (MCP's tool hints)
  - `AbilitiesMcpProxyTests.An_MCP_client_is_told_which_tools_only_read_and_which_need_a_person_to_approve_them`
- The proxy asks the person the site's question (MCP elicitation) before a call that can't be undone; building a page asks once, at apply, listing the page
  - `AbilitiesMcpProxyTests.An_MCP_client_builds_a_page_in_a_change_set_without_naming_it_and_it_is_saved_on_apply`
- A person who says no stops the call, and nothing is saved; a client that can't ask is refused
  - `AbilitiesMcpProxyTests.A_person_who_says_no_stops_a_call_that_cannot_be_undone_and_nothing_is_saved`
  - `AbilitiesMcpProxyTests.A_client_that_cannot_ask_the_person_is_refused_a_call_that_cannot_be_undone`
- An MCP client builds a page in a change set without naming it on each call: the session remembers the set it opened, and closes it once applied
  - `AbilitiesMcpProxyTests.An_MCP_client_builds_a_page_in_a_change_set_without_naming_it_and_it_is_saved_on_apply`
- A refused call is an MCP tool error, with the problems to fix
  - `AbilitiesMcpProxyTests.A_refused_call_is_an_MCP_tool_error_with_the_problems_to_fix`

## Schemas are real JSON Schema

- Every tool's input schema, and every entity, subtype and item type schema at any depth, is valid JSON Schema 2020-12 with only the framework's own x- notes, used correctly
  - `SchemaMetaschemaTests.Every_tools_input_schema_is_valid_JSON_Schema`
  - `SchemaMetaschemaTests.Every_entity_schema_and_item_type_schema_is_valid_JSON_Schema`
  - `CmsDocumentAdapterTests.Every_document_and_media_schema_and_block_schema_is_valid_JSON_Schema`
- The check catches a mistake two levels down (a bad type, a null description, an unknown or misused x- note, an editor without an alias, a badly named metadata key, a format that is neither standard nor owned), and accepts a correct schema
  - `SchemaMetaschemaTests.The_meta_schema_catches_a_mistake_in_a_field_deep_inside_a_schema`
  - `SchemaMetaschemaTests.The_meta_schema_accepts_a_correct_schema_with_the_frameworks_keywords`

## Hints for UIs

- A field can name its own editor and carry hints that a UI sees and a model does not, two levels down in a block's settings
  - `UiHintsTests.A_field_can_name_its_own_editor_and_carry_hints_that_a_UI_sees_and_a_model_does_not`
- An ability's input can name its editor, for a form built from its schema
  - `UiHintsTests.An_abilitys_input_can_name_its_editor_for_a_form_built_from_its_schema`
- The fluent set-up names an editor and adds hints, like the attributes
  - `UiHintsTests.The_fluent_set_up_names_an_editor_and_adds_hints_too`
- A metadata key is `owner.name` in lower case, its value an object of at most 4 KB
  - `UiHintsTests.A_metadata_key_is_owner_dot_name_in_lower_case_and_its_value_is_an_object`
  - `UiHintsTests.A_metadata_value_over_4_KB_is_refused_because_the_schema_is_read_on_every_call`
- A form picks each editor from the field, then the format, then the shape, and skips an editor that isn't installed
  - `UiHintsTests.A_form_picks_each_editor_from_the_field_then_the_format_then_the_shape`
- The hints change nothing but how a field looks: validation ignores them
  - `UiHintsTests.Deleting_every_hint_changes_nothing_but_how_a_field_looks`
- Every real document field names its data type's editor and key, and the model sees neither
  - `CmsDocumentAdapterTests.Each_field_names_its_data_types_editor_for_a_UI_and_a_model_sees_neither`
- A value type names its format once, and every field of that type has it; a model sees it
  - `ValueFormatTests.A_value_type_names_its_format_once_and_every_setting_of_that_type_has_it`
  - `ValueFormatTests.An_input_property_can_name_a_standard_format_and_a_model_sees_it`
  - `ValueFormatTests.The_fluent_set_up_names_a_format_too`
- A format is one of JSON Schema's own, or `owner.name` in lower case
  - `ValueFormatTests.A_format_is_one_of_JSON_Schemas_own_or_owner_dot_name_in_lower_case`
  - `ValueFormatTests.A_standard_format_or_a_packages_own_is_accepted`
- A form maps a format to one editor for every field of that kind, and shows a text box when it has none
  - `ValueFormatTests.A_form_maps_a_format_to_one_editor_for_every_field_of_that_kind`
- A product's own editor for a setting becomes the field's editor, for a UI only, in the samples and on real Commerce
  - `ValueFormatTests.A_products_own_editor_for_a_setting_becomes_the_fields_editor`
  - `CommerceDiscountAdapterTests.Each_rule_and_reward_setting_names_Commerces_own_editor_for_a_UI_and_not_for_the_model`
- A consumer that builds forms gets the hints over the Management API; the MCP server's consumer gets schemas and results without them
  - `AbilitiesManagementApiTests.A_consumer_that_builds_forms_gets_each_fields_editor_and_the_MCP_server_does_not`
  - `AbilitiesManagementApiTests.Only_a_model_is_told_to_move_a_page_with_entity_move`
  - `AbilitiesMcpProxyTests.An_MCP_client_gets_schemas_without_the_hints_meant_for_UIs`

## Consumers and filters

See [CONSUMERS.md](CONSUMERS.md).

- A consumer that can ask and keeps a conversation offers every ability
  - `ConsumerTests.A_consumer_that_can_ask_and_keeps_a_conversation_offers_every_ability`
- A package adds a filter, and a consumer applies it, with no framework change
  - `ConsumerTests.A_package_adds_a_filter_and_a_consumer_applies_it_without_any_framework_change`
- A package adds a consumer and gets its list from one policy
  - `ConsumerTests.A_package_adds_a_consumer_and_gets_a_list_from_one_policy`
- Filters run in the order the consumer lists them
  - `ConsumerTests.Filters_run_in_the_order_the_consumer_lists_them`
- A filter added to a policy reaches every consumer that applies it, and its note names the policy
  - `ConsumerTests.A_filter_added_to_a_policy_reaches_every_consumer_that_applies_it`
- A policy that lists itself, or a name that is neither a filter nor a policy, is refused when the site starts
  - `ConsumerTests.A_policy_that_lists_itself_is_refused_at_start_up`
  - `ConsumerTests.A_consumer_listing_a_filter_nobody_added_is_refused_at_start_up`
- A package adds a filter to another package's consumer: it runs after the consumer's own, and its note names the package
  - `ConsumerTests.A_package_adds_a_filter_to_another_packages_consumer_and_its_note_names_the_package`
- The base policy runs for every consumer, whatever it lists: what can't be undone is left out where nobody can agree to
  it, and the change set tools where no conversation keeps a set open
  - `ConsumerTests.An_ability_that_needs_approval_is_dropped_where_nobody_can_agree_to_it_whatever_the_consumer_applies`
  - `ConsumerTests.The_change_set_tools_are_dropped_where_no_conversation_keeps_a_set_open`
- An ability that needs something a package names, such as sending a file, is offered only where the consumer says it can
  do it
  - `ConsumerTests.An_ability_that_needs_a_capability_a_package_names_is_offered_only_where_the_consumer_states_it`
- Every dropped tool stays listed with why; only kept tools are offered
  - `ConsumerTests.Every_dropped_tool_stays_listed_with_why_and_only_kept_tools_are_offered`
- A filter can put several tools in place of one, each calling its ability with a fixed argument the caller doesn't send
  - `ConsumerTests.A_filter_can_put_several_tools_in_place_of_one_each_calling_its_ability_with_a_fixed_argument`
- The AI chat consumer offers every ability, as before consumers, with `changeSetId` where the caller chooses the set
  - `ConsumerTests.An_AI_chat_offers_every_ability_as_before_with_changeSetId_where_the_caller_chooses_the_set`
- A form consumer (the `for-forms` policy) gets whole operations: no building blocks, change set or Explain abilities
  - `ConsumerTests.A_form_consumer_gets_whole_operations_and_no_building_blocks_change_set_or_explain_abilities`
- A form consumer's tools take no `changeSetId`, and each Write is a change set of its own
  - `ConsumerTests.A_form_consumer_gets_no_changeSetId_and_its_writes_are_each_a_change_set_of_their_own`
  - `ConsumerTests.A_call_in_its_own_change_set_is_applied_at_once_and_one_that_fails_leaves_nothing_behind`
- A model consumer's tools have no keywords meant for forms; a form consumer's keep them
  - `ConsumerTests.A_model_consumers_tools_have_no_keywords_meant_for_forms`
- An admin leaves an ability out of one consumer only, and can bring back what a filter dropped but not what the base policy dropped
  - `ConsumerTests.An_admin_can_leave_an_ability_out_of_one_consumer_only`
  - `ConsumerTests.An_admin_can_bring_back_what_a_filter_dropped_but_not_what_the_base_policy_dropped`
- A model reads each description with model hints, and a person with person hints, on abilities and on their inputs
  - `ConsumerTests.A_model_reads_the_description_with_model_hints_and_a_person_with_person_hints`
- Hints follow the audience, not the consumer: two model consumers with different policies read the same text
  - `ConsumerTests.Two_consumers_with_the_same_audience_get_the_same_hints`
- A schema a model reads back from `entity.getSchema` has model hints only, and no `x-hints`
  - `ConsumerTests.A_model_reading_a_schema_back_gets_model_hints_only`
- A form consumer gets a Create and an Update per entity type, with the type fixed; a model gets one of each
  - `ConsumerTests.A_form_consumer_gets_a_Create_and_an_Update_per_entity_type_and_a_model_one_of_each`
- An update changes only the values given, in one change set
  - `ConsumerTests.An_update_changes_only_the_values_given_in_one_change_set`
- A form resolves the fields input to the entity type's fields, with person hints: required on Create, none on Update
  - `ConsumerTests.A_form_resolves_the_fields_input_to_the_entity_types_fields_with_person_hints`
  - `AbilitiesManagementApiTests.A_form_gets_a_tools_input_with_the_fields_of_the_page_type_chosen`
- A page type used only inside others can't be created, and `entity.getSchema` says so
  - `CmsDocumentAdapterTests.A_page_type_only_used_inside_others_cannot_be_created`
- The consumer named in the `Umb-Consumer` header decides the list (`mcp` without it), and the list answers with `Vary`
  - `AbilitiesManagementApiTests.The_consumer_named_in_a_header_decides_the_list_and_a_cache_keeps_one_per_consumer`
- A tool a consumer doesn't offer can't be called through it over the Management API
  - `AbilitiesManagementApiTests.A_tool_the_consumer_does_not_offer_cannot_be_called_through_it`

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
- A tree picker is a list of page keys marked `x-references`, its description says the allowed type, start node and count, find lists pages by type, a key that isn't a page is caught by validation, and the key is saved in the picker's own format and reads back as the key
  - `CmsDocumentAdapterTests.A_tree_picker_is_a_list_of_page_references_saved_in_Umbracos_own_format`
- A page allowed only under a parent is created under it, reads back in the set with its parent, and keeps that parent when edited again
  - `CmsDocumentAdapterTests.A_page_allowed_only_under_a_parent_is_created_under_it_and_reads_back_in_the_set`
- A page moved in a set is under its new parent in the set, under the old one live, and is moved by Umbraco on apply, including under a parent created in the same set
  - `CmsDocumentAdapterTests.A_page_moved_in_a_change_set_is_under_its_new_parent_in_the_set_and_moves_when_applied`
  - `CmsDocumentAdapterTests.A_page_can_be_moved_under_a_parent_created_in_the_same_set`
- A move Umbraco would refuse (a type not allowed at the root, a page under itself) is a problem on `parent` before applying
  - `CmsDocumentAdapterTests.A_move_Umbraco_would_refuse_is_a_problem_on_the_parent_before_it_is_applied`
- A page deleted in a set is gone in the set, live outside it, and goes to the recycle bin on apply
  - `CmsDocumentAdapterTests.A_page_deleted_in_a_change_set_is_gone_in_the_set_and_goes_to_the_recycle_bin_when_applied`
  - `CmsDocumentAdapterTests.A_page_created_and_deleted_in_one_set_is_never_saved`
- A page moved out of a page deleted in the same set is moved first, and not deleted with it
  - `CmsDocumentAdapterTests.A_page_moved_out_of_a_page_deleted_in_the_same_set_is_moved_first_and_not_deleted`
- Moving takes the CMS's move permission on the page and create permission on the new parent, deleting its delete permission, whether through `entity.move` or `entity.setValue`
  - `CmsDocumentAdapterTests.Moving_and_deleting_take_the_CMS_permissions_for_them_however_the_change_is_made`
- A page moved both in the set and in the backoffice is a conflict on `parent`, resolved like any value
  - `CmsDocumentAdapterTests.A_page_moved_in_both_the_set_and_the_backoffice_is_a_conflict_on_its_parent`

## Against real Umbraco Commerce

- A page and a discount built in one change set are applied together: content first, then Commerce, each in its product's transaction, and neither is saved before apply
  - `CommerceDiscountAdapterTests.A_page_and_a_discount_built_in_one_change_set_are_applied_together_each_in_its_products_transaction`
- A discount Commerce refuses stops the whole set, so the page beside it isn't saved either
  - `CommerceDiscountAdapterTests.A_discount_Commerce_refuses_stops_the_whole_set_so_the_page_beside_it_is_not_saved_either`
- A discount built in a change set through the AI tools is saved in Commerce only when the set is applied, with its codes, dates, rules and rewards
  - `CommerceDiscountAdapterTests.A_discount_built_in_a_change_set_is_saved_in_Commerce_only_when_the_set_is_applied`
- The rules and rewards offered are Commerce's own providers, a group rule included
  - `CommerceDiscountAdapterTests.The_rules_and_rewards_offered_are_Commerces_own_providers`
- An amount is keyed by a currency the model finds with `entity_find`, and reads back as an object
  - `CommerceDiscountAdapterTests.An_amount_is_keyed_by_a_currency_the_model_finds_and_reads_back_as_an_object`
- Settings are stored as Commerce's own editors store them (`TotalPrice`, amounts as numbers), so the backoffice shows them
  - `CommerceDiscountAdapterTests.Settings_are_stored_as_Commerces_own_editors_store_them_so_the_backoffice_shows_them`
- A code another discount already uses is a problem from Commerce's own validation before the set is applied, and the check's save is rolled back
  - `CommerceDiscountAdapterTests.A_code_another_discount_already_uses_is_a_problem_from_Commerce_before_the_set_is_applied`
- A user without the Commerce section can't make a discount
  - `CommerceDiscountAdapterTests.A_user_without_the_Commerce_section_cannot_make_a_discount`

## Against real Umbraco Automate

- Every whole ability is an Automate action beside Automate's own. The change set abilities and the building blocks a model chains (`entity.setValue`, `entity.addItem`, ...) are not
  - `AbilityActionTests.Every_whole_ability_is_an_Automate_action_beside_Automates_own_and_building_blocks_are_not`
- Every action's output schema is an object, or none, as Automate's action list reads it: one that isn't breaks the list in its editor
  - `AbilityActionTests.Every_actions_output_schema_is_an_object_or_none_as_Automates_action_list_reads_it`
- Automate lists one Create and one Update per entity type, and the page type is picked in the form
  - `AbilityActionTests.Automate_lists_one_Create_and_one_Update_per_entity_type_and_the_page_type_is_picked_in_the_form`
- Choosing a page type gives a form of its fields, with Umbraco's editors and their data type configuration
  - `AbilityActionTests.Choosing_a_page_type_gives_a_form_of_its_fields_with_their_editors`
- A page type added later can be chosen without a restart
  - `AbilityActionTests.A_page_type_added_later_can_be_chosen_without_a_restart`
- A composition page type can't be chosen to create
  - `AbilityActionTests.A_composition_page_type_cannot_be_chosen_to_create`
- An Update form picks the page and asks for no value: one left empty stays as it is
  - `AbilityActionTests.An_Update_form_picks_the_page_and_asks_for_no_value`
- A Create action returns a typed summary a later step can bind to
  - `AbilityActionTests.A_Create_action_returns_a_typed_summary_a_later_step_can_bind_to`
- An automation creates a page from Umbraco's editor values (a dropdown's and a tree picker's), then updates it by binding to its key, as the service account
  - `AbilityActionTests.An_automation_creates_a_page_from_Umbracos_editor_values_then_updates_it_as_the_service_account`
- A value the page type refuses fails the step with the field's path, and nothing is saved
  - `AbilityActionTests.A_value_the_page_type_refuses_fails_the_step_with_the_fields_path_and_saves_nothing`
